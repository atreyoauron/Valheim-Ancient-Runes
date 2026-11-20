using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace BossMagic.Test
{
    internal static class BeaconSystem
    {
        internal const string RuneName = "BossMagic_BeaconRune";
        internal static ConfigEntry<float> Damage, Cost, Cooldown, Range;
        internal static bool Ready;
        internal static float NextCast;
        internal static int CastCount;
        private static Player lastCaster;
        // Local RPC dispatch is synchronous in this game's singleplayer implementation.
        // Tag the deserialized hit at the receiving endpoint, never another game's generic damage.
        private static Character pendingTarget;
        private static Player pendingCaster;
        private static bool pendingConsumed;
        private static readonly ConditionalWeakTable<HitData, object> pureHits = new ConditionalWeakTable<HitData, object>();
        internal static void Initialize(Plugin host, Harmony harmony)
        {
            Damage = Bind(host, "PureDamage", 100, 1, 5000, "Dano puro base. Ignora armadura/resistencias por tipo; modificadores gerais continuam validos.");
            Cost = Bind(host, "ActivationStamina", 25, 0, 200, "Custo por raio. Sem alvo valido, nao cobra estamina.");
            Cooldown = Bind(host, "Cooldown", 6, 0, 120, "Recarga em segundos, contada desde o raio.");
            Range = Bind(host, "Range", 50, 5, 100, "Alcance em metros. Exige inimigo na mira, linha de visao e 30 metros livres acima do alvo.");
            PrefabManager.OnVanillaPrefabsAvailable += Register;
            CommandManager.Instance.AddConsoleCommand(new BeaconCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "UseItem"), prefix: new HarmonyMethod(typeof(BeaconSystem), nameof(Use)));
            harmony.Patch(AccessTools.Method(typeof(Character), "RPC_Damage"), prefix: new HarmonyMethod(typeof(BeaconSystem), nameof(TagReceivedHit)));
            harmony.Patch(AccessTools.Method(typeof(HitData), "ApplyModifier", new[] { typeof(float) }), postfix: new HarmonyMethod(typeof(BeaconSystem), nameof(ModifyPureDamage)));
        }
        private static ConfigEntry<float> Bind(Plugin host, string key, float value, float min, float max, string text)
            => host.Config.Bind("Beacon", key, value, new ConfigDescription(text, new AcceptableValueRange<float>(min, max),new ConfigurationManagerAttributes { IsAdminOnly=true }));
        private static void Register()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig { Name = "Runa de Lightning Beacon", Description = "Invoca um raio do ceu sobre o inimigo na mira. Dano puro, sem explosao em area. Requer caminho livre e alvo descoberto. Use na hotbar mantendo suas armas." });
            var shared = rune.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material; shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = .2f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                float r = new Vector2(x - 32, y - 32).magnitude;
                bool bolt = y > 14 && y < 55 && Math.Abs(x - (27 + y % 13)) < 3;
                bool ring = Mathf.Abs(Mathf.Pow((x - 32f) / 19, 2) + Mathf.Pow((y - 14f) / 6, 2) - 1) < .25f;
                pixels[y * 64 + x] = r > 29 ? Color.clear : bolt || ring ? new Color(.9f, 1, 1) : new Color(.12f, .17f, .32f);
            }
            texture.SetPixels(pixels); texture.Apply();
            shared.m_icons = new[] { Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f)) };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Beacon registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            Plugin.instance.InfoLog("BOSSMAGIC_BEACON_REGISTERED");
        }
        private static bool Use(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != RuneName) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory()) && player.GetInventory().ContainsItem(item)) TryCast(player);
            return false;
        }
        internal static Character FindTarget(Player player, out string reason)
        {
            reason = "Mire diretamente em um inimigo.";
            if (!Camera.main) return null;
            var ray = Camera.main.ViewportPointToRay(new Vector3(.5f, .5f));
            foreach (var hit in Physics.RaycastAll(ray, Range.Value + Vector3.Distance(ray.origin, player.GetEyePoint()), MagicSystem.CollisionMask, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance)) {
                var target = hit.collider.GetComponentInParent<Character>();
                if (target == player) continue;
                if (!MagicSystem.IsTarget(target, player)) return null;
                if (Vector3.Distance(player.GetEyePoint(), hit.point) > Range.Value) { reason = "Alvo fora do alcance."; return null; }
                if (Physics.Linecast(player.GetEyePoint(), hit.point, MagicSystem.WorldMask, QueryTriggerInteraction.Ignore)) { reason = "O caminho ate o alvo esta bloqueado."; return null; }
                if (Physics.Raycast(target.GetCenterPoint(), Vector3.up, 30, MagicSystem.WorldMask, QueryTriggerInteraction.Ignore)) { reason = "O alvo esta sob cobertura."; return null; }
                return target;
            }
            return null;
        }
        internal static bool TryCast(Player player)
        {
            if (!ZNet.instance) return false;
            if (player.IsDead() || player.InCutscene() || player.IsTeleporting() || player.IsAttached() || player.InAttack() || player.IsDodgeInvincible() || player.IsStaggering()) return false;
            if (lastCaster != player) { NextCast = 0; lastCaster = player; }
            if (Time.time < NextCast) { MagicSystem.Say(player, "Beacon recarregando."); return false; }
            var target = FindTarget(player, out var reason);
            if (!target) { MagicSystem.Say(player, reason); return false; }
            if (!player.HaveStamina(Cost.Value)) { MagicSystem.Say(player, "Estamina insuficiente para Beacon."); return false; }
            BeaconEffects.Prepare();
            var point = target.transform.position;
            player.UseStamina(Cost.Value); NextCast = Time.time + Cooldown.Value;
            NetworkMagic.Beacon(player,target);
            BeaconEffects.Play(point); NetworkMagic.BeaconVisual(player,point);
            CastCount++;
            Plugin.instance.InfoLog("BOSSMAGIC_BEACON_CAST target=" + target.name + " pureBase=" + Damage.Value);
            return true;
        }
        internal static HitData MakeHit(Player player, Character target, float amount)
        {
            var hit = new HitData { m_point = target.GetCenterPoint(), m_dir = Vector3.down, m_ranged = true,
                m_dodgeable = true, m_blockable = false, m_backstabBonus = 1, m_staggerMultiplier = 0, m_pushForce = 0 };
            hit.m_damage.m_damage = amount; hit.SetAttacker(player);
            return hit;
        }
        internal static void ApplyPureHit(Player player, Character target, float amount)
        {
            var previousTarget = pendingTarget; var previousCaster = pendingCaster; bool previousConsumed = pendingConsumed;
            pendingTarget = target; pendingCaster = player; pendingConsumed = false;
            try { target.Damage(MakeHit(player, target, amount)); }
            finally { pendingTarget = previousTarget; pendingCaster = previousCaster; pendingConsumed = previousConsumed; }
        }
        private static void TagReceivedHit(Character __instance, HitData __1)
        {
            if (pendingConsumed || !pendingTarget || __instance != pendingTarget || __1.GetAttacker() != pendingCaster || __1.m_damage.m_damage <= 0) return;
            pendingConsumed = true; pureHits.GetValue(__1, _ => new object());
        }
        private static void ModifyPureDamage(HitData __instance, float __0)
        {
            if (pureHits.TryGetValue(__instance, out var marker)) __instance.m_damage.m_damage *= __0;
        }
        internal static ItemDrop.ItemData Give(Player player)
        {
            var prefab = PrefabManager.Instance.GetPrefab(RuneName);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab;
            if (!player.GetInventory().AddItem(item)) { MagicSystem.Say(player, "Inventario cheio."); return null; }
            return item;
        }
        private sealed class BeaconCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_beacon";
            public override string Help => "Entrega a runa Lightning Beacon experimental.";
            public override void Run(string[] args) { if (Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }
}
