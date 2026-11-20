using System;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace BossMagic.Test
{
    internal static class SurgeSystem
    {
        internal const string RuneName = "BossMagic_SurgeRune";
        internal enum StaminaMode { ActivationOnly, AdditionalSprintDrain }
        internal static ConfigEntry<StaminaMode> Mode;
        internal static ConfigEntry<float> Duration, Cost, Multiplier, ExtraDrain, Cooldown;
        internal static bool Ready;
        internal static Player Owner;
        internal static float EndsAt, NextCast;
        internal static StaminaMode ActiveMode;
        internal static float ActiveMultiplier, ActiveDrain;
        internal static GameObject Aura;
        private static Player lastOwner;
        private static Plugin host;
        internal static bool Active => Owner != null && Aura != null;

        internal static void Initialize(Plugin plugin, Harmony harmony)
        {
            host = plugin;
            Mode = host.Config.Bind("Surge", "StaminaMode", StaminaMode.ActivationOnly,
                new ConfigDescription("ActivationOnly: custo inicial e corrida gratuita. AdditionalSprintDrain: custo inicial, corrida normal e gasto adicional enquanto corre. Alteracoes valem na proxima ativacao.",null,new ConfigurationManagerAttributes { IsAdminOnly=true }));
            Duration = Bind("Duration", 10, 1, 60, "Duracao em segundos, nos dois modos.");
            Cost = Bind("ActivationStamina", 20, 0, 200, "Estamina gasta uma vez na ativacao, nos dois modos.");
            Multiplier = Bind("RunSpeedMultiplier", 1.8f, 1, 4, "Multiplicador da velocidade de corrida; nao altera caminhada.");
            ExtraDrain = Bind("AdditionalStaminaPerSecond", 6, 0, 50, "Gasto adicional por segundo de corrida apenas no modo AdditionalSprintDrain.");
            Cooldown = Bind("Cooldown", 15, 0, 120, "Recarga em segundos contada desde a ativacao; nao permite acumular efeitos.");
            PrefabManager.OnVanillaPrefabsAvailable += Register;
            CommandManager.Instance.AddConsoleCommand(new SurgeCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "UseItem"), prefix: new HarmonyMethod(typeof(SurgeSystem), nameof(UseRune)));
            harmony.Patch(AccessTools.Method(typeof(Player), "GetRunSpeedFactor"), postfix: new HarmonyMethod(typeof(SurgeSystem), nameof(Speed)));
            harmony.Patch(AccessTools.Method(typeof(SEMan), "ModifyRunStaminaDrain"), postfix: new HarmonyMethod(typeof(SurgeSystem), nameof(Drain)));
        }
        private static ConfigEntry<float> Bind(string key, float value, float min, float max, string description)
            => host.Config.Bind("Surge", key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max),new ConfigurationManagerAttributes { IsAdminOnly=true }));

        private static void Register()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig { Name = "Runa de Eikthyr Surge", Description = "Envolve voce em eletricidade e acelera a corrida. Use na hotbar mantendo suas armas. Configure pelo F1." });
            var shared = rune.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material;
            shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = 0.2f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                float r = new Vector2(x - 32, y - 32).magnitude;
                bool chevron = (Math.Abs(x - (20 + Math.Abs(y - 32) / 2)) < 3 || Math.Abs(x - (34 + Math.Abs(y - 32) / 2)) < 3) && y > 14 && y < 50;
                pixels[y * 64 + x] = r > 29 ? Color.clear : chevron ? Color.cyan : new Color(.08f, .18f, .23f, 1);
            }
            texture.SetPixels(pixels); texture.Apply();
            shared.m_icons = new[] { Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f)) };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Surge rune registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            host.InfoLog("BOSSMAGIC_SURGE_REGISTERED");
        }

        private static bool UseRune(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != RuneName) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory()) && player.GetInventory().ContainsItem(item)) TryCast(player);
            return false;
        }
        internal static bool TryCast(Player player)
        {
            if (!ZNet.instance) return false;
            if (player.IsDead() || player.InCutscene() || player.IsTeleporting() || player.IsAttached() || player.InAttack() || player.IsDodgeInvincible() || player.IsStaggering()) return false;
            if (lastOwner != player) { Stop(); NextCast = 0; lastOwner = player; }
            if (Active || Time.time < NextCast) { player.Message(MessageHud.MessageType.Center, "Surge ativo ou recarregando."); return false; }
            if (!player.HaveStamina(Cost.Value)) { player.Message(MessageHud.MessageType.Center, "Estamina insuficiente para Surge."); return false; }
            Owner = player; ActiveMode = Mode.Value; ActiveMultiplier = Multiplier.Value; ActiveDrain = ExtraDrain.Value;
            EndsAt = Time.time + Duration.Value; NextCast = Time.time + Cooldown.Value;
            Aura = new GameObject("BossMagic_SurgeAura");
            Aura.transform.SetParent(player.transform, false);
            Aura.AddComponent<SurgeAura>();
            player.UseStamina(Cost.Value);
            player.Message(MessageHud.MessageType.Center, "Eikthyr Surge: " + Duration.Value.ToString("0") + "s");
            host.InfoLog("BOSSMAGIC_SURGE_CAST mode=" + ActiveMode + " duration=" + Duration.Value);
            return true;
        }
        internal static void Tick()
        {
            if (Owner == null) { if (Aura) Stop(); return; }
            if (!Aura || Owner != Player.m_localPlayer || Owner.IsDead() || Owner.IsTeleporting() || Owner.IsAttached() || Time.time >= EndsAt ||
                !ZNet.instance || (ActiveMode == StaminaMode.AdditionalSprintDrain && !Owner.HaveStamina(0))) Stop();
        }
        internal static void Stop()
        {
            if (Aura) UnityEngine.Object.Destroy(Aura);
            Aura = null; Owner = null;
        }
        private static void Speed(Player __instance, ref float __result)
        {
            if (Active && Owner == __instance) __result *= ActiveMultiplier;
        }
        private static void Drain(SEMan __instance, ref float __1)
        {
            if (!Active || Owner.GetSEMan() != __instance) return;
            __1 = ActiveMode == StaminaMode.ActivationOnly ? 0 : __1 + ActiveDrain;
        }
        internal static void Give(Player player)
        {
            var prefab = PrefabManager.Instance.GetPrefab(RuneName);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab;
            if (!player.GetInventory().AddItem(item)) player.Message(MessageHud.MessageType.Center, "Inventario cheio.");
        }
        private sealed class SurgeCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_surge";
            public override string Help => "Entrega a runa Surge experimental.";
            public override void Run(string[] args) { if (Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }

    public sealed class SurgeAura : MonoBehaviour
    {
        internal static string SourceName;
        private void Awake()
        {
            // Resolve the same status effect that Character.AddLightningDamage uses.
            int hash = (int)AccessTools.Field(typeof(SEMan), "s_statusEffectLightning").GetValue(null);
            var status = ObjectDB.instance.GetStatusEffect(hash);
            if (!status) throw new InvalidOperationException("Native Lightning status not found");
            var staging = new GameObject("SurgeInactiveStaging"); staging.SetActive(false);
            try {
                foreach (var entry in status.m_startEffects.m_effectPrefabs) {
                    if (!entry.m_enabled || !entry.m_prefab || entry.m_prefab.name != "fx_Lightning") continue;
                    SourceName = entry.m_prefab.name;
                    Plugin.instance.InfoLog("BOSSMAGIC_SURGE_NATIVE source=" + SourceName + " components=" + string.Join(",", Array.ConvertAll(entry.m_prefab.GetComponentsInChildren<Component>(true), c => c ? c.GetType().Name : "null")));
                    var effect = Instantiate(entry.m_prefab, staging.transform, false);
                    foreach (var script in effect.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(script);
                    foreach (var collider in effect.GetComponentsInChildren<Collider>(true)) DestroyImmediate(collider);
                    foreach (var body in effect.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(body);
                    foreach (var audio in effect.GetComponentsInChildren<AudioSource>(true)) DestroyImmediate(audio);
                    effect.SetActive(false);
                    effect.transform.SetParent(transform, false);
                    effect.transform.localPosition = transform.InverseTransformPoint(GetComponentInParent<Character>().GetCenterPoint());
                    if (entry.m_scale) effect.transform.localScale = Vector3.one * GetComponentInParent<Character>().GetRadius() * 2f;
                    foreach (var particles in effect.GetComponentsInChildren<ParticleSystem>(true)) {
                        var main = particles.main; main.loop = true; main.stopAction = ParticleSystemStopAction.None;
                        Plugin.instance.InfoLog("BOSSMAGIC_SURGE_PARTICLE " + particles.name + " shape=" + particles.shape.shapeType);
                    }
                    effect.SetActive(true);
                    foreach (var particles in effect.GetComponentsInChildren<ParticleSystem>()) particles.Play();
                }
                if (transform.childCount == 0) throw new InvalidOperationException("Native Lightning visual missing");
            } finally { Destroy(staging); }
        }
    }
}

