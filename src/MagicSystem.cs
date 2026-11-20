using System;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace BossMagic.Test
{
    internal static class MagicSystem
    {
        internal const string RuneName = "BossMagic_LightningRune";
        internal static bool RuneReady;
        internal enum TrajectoryMode { Straight, Arrow }
        internal static ConfigEntry<TrajectoryMode> Trajectory;
        internal static ConfigEntry<float> Damage, Radius, Push, Speed, Cost, Cooldown, Gravity;
        internal static float NextCast;
        internal static int CastCount, ImpactCount, DamageCount;
        internal static Vector3 LastImpact;
        internal static Player LastCaster;
        private static Plugin plugin;
        internal static Material Glow;
        internal static readonly int WorldMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
        internal static readonly int CollisionMask = WorldMask | LayerMask.GetMask("character", "character_net", "character_ghost", "hitbox");

        internal static void Initialize(Plugin host, Harmony harmony)
        {
            plugin = host;
            Damage = Bind("Damage", 25f, 1f, 500f, "Dano eletrico por impacto.");
            Radius = Bind("Radius", 3f, 0.5f, 10f, "Raio do impacto em metros.");
            Push = Bind("Push", 20f, 0f, 100f, "Forca de empurrao; a resistencia do alvo influencia o resultado.");
            Speed = Bind("Speed", 30f, 5f, 80f, "Velocidade do projetil em metros por segundo.");
            Cost = Bind("Stamina", 10f, 0f, 100f, "Custo de estamina por disparo.");
            Cooldown = Bind("Cooldown", 2.5f, 0.2f, 30f, "Recarga compartilhada entre todas as runas eletricas.");
            Trajectory = plugin.Config.Bind("Lightning", "Trajectory", TrajectoryMode.Straight, new ConfigDescription("Straight: segue a mira sem queda. Arrow: trajetoria com queda suave, como uma flecha.",null,new ConfigurationManagerAttributes { IsAdminOnly=true }));
            Gravity = Bind("Gravity", 4f, 0f, 30f, "Aceleracao de queda em m/s2, aplicada apenas no modo Arrow.");
            PrefabManager.OnVanillaPrefabsAvailable += RegisterRune;
            CommandManager.Instance.AddConsoleCommand(new RuneCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "UseItem"), prefix: new HarmonyMethod(typeof(MagicSystem), nameof(UseRune)));
        }

        private static ConfigEntry<float> Bind(string key, float initial, float min, float max, string description)
            => plugin.Config.Bind("Lightning", key, initial, new ConfigDescription(description, new AcceptableValueRange<float>(min, max),new ConfigurationManagerAttributes { IsAdminOnly=true }));

        private static void RegisterRune()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig {
                Name = "Runa do Trovao", Description = "Canaliza um projetil eletrico pela mira. Use na hotbar sem guardar suas armas. Reutilizavel. Prototipo do laboratorio."
            });
            var data = rune.ItemDrop.m_itemData.m_shared;
            data.m_itemType = ItemDrop.ItemData.ItemType.Material;
            data.m_maxStackSize = 1;
            data.m_maxQuality = 1;
            data.m_weight = 0.2f;
            data.m_teleportable = true;
            data.m_icons = new[] { CreateIcon() };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Failed to register lightning rune.");
            RuneReady = true;
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterRune;
            plugin.InfoLog("BOSSMAGIC_RUNE_REGISTERED " + RuneName);
        }

        private static Sprite CreateIcon()
        {
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            texture.name = "BossMagic_RuneIcon";
            var colors = new Color[64 * 64];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                float dx = (x - 32f) / 25f, dy = (y - 31f) / 29f;
                float dist = dx * dx + dy * dy;
                Color color = dist < 1 ? new Color(0.11f, 0.18f, 0.25f, 1f) : Color.clear;
                if (dist > 0.77f && dist < 0.9f) color = new Color(0.1f, 0.55f, 0.8f, 1f);
                if ((y >= 30 && y <= 53 && x >= 24 + (y - 30) / 2 && x <= 35 + (y - 30) / 2) ||
                    (y >= 10 && y < 34 && x >= 18 + (y - 10) / 2 && x <= 29 + (y - 10) / 2))
                    color = new Color(0.65f, 0.95f, 1f, 1f);
                colors[y * 64 + x] = color;
            }
            texture.SetPixels(colors); texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f));
        }

        internal static bool IsRune(ItemDrop.ItemData item) => item?.m_dropPrefab != null && item.m_dropPrefab.name == RuneName;

        private static bool UseRune(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (!IsRune(item)) return true;
            var player = __instance as Player;
            if (player != Player.m_localPlayer || player == null) return false;
            if (inventory != null && inventory != player.GetInventory()) return false;
            if (!player.GetInventory().ContainsItem(item)) return false;
            TryCast(player);
            return false; // Never call vanilla equip/consume for this item.
        }

        internal static bool TryCast(Player player)
        {
            if (!ZNet.instance) {
                Say(player, "Entre em um mundo para usar a runa."); return false;
            }
            if (player.IsDead() || player.InCutscene() || player.IsTeleporting() || player.IsAttached() ||
                player.InAttack() || player.IsDodgeInvincible() || player.IsStaggering()) return false;
            if (LastCaster != player) { NextCast = 0f; LastCaster = player; }
            if (Time.time < NextCast) { Say(player, "Runa recarregando: " + (NextCast - Time.time).ToString("0.0") + "s"); return false; }
            if (!player.HaveStamina(Cost.Value)) { Say(player, "Estamina insuficiente para a runa."); return false; }
            Vector3 start = player.GetEyePoint() + Vector3.down * 0.25f;
            Vector3 direction = player.GetLookDir();
            var camera = Camera.main;
            if (camera) {
                var ray = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f));
                Vector3 target = ray.GetPoint(60f);
                foreach (var hit in Physics.RaycastAll(ray, 60f, CollisionMask, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance)) {
                    var character = hit.collider.GetComponentInParent<Character>();
                    if (character == player || (character && !IsTarget(character, player))) continue;
                    if (Vector3.Dot(hit.point - start, player.GetLookDir()) < 0.1f) continue;
                    target = hit.point; break;
                }
                direction = (target - start).normalized;
            }
            var projectile = new GameObject("BossMagic_LightningMissile");
            projectile.transform.position = start;
            projectile.AddComponent<LightningMissile>().Initialize(player, direction);
            NetworkMagic.Projectile(1,player,start,direction);
            player.UseStamina(Cost.Value);
            NextCast = Time.time + Cooldown.Value;
            CastCount++;
            plugin.InfoLog("BOSSMAGIC_CAST count=" + CastCount + " weapons_preserved=true");
            return true;
        }

        internal static bool IsTarget(Character target, Player owner) => target && target != owner && !(target is Player) &&
            !target.IsDead() && !target.IsTamed() && BaseAI.IsEnemy(owner, target);
        internal static void Say(Player player, string text) => player.Message(MessageHud.MessageType.Center, text);

        internal static void Impact(Player owner, Vector3 point, Vector3 normal, float damage, float radius, float push)
        {
            LastImpact = point; ImpactCount++;
            var targets = Character.GetAllCharacters().ToArray();
            int count = 0;
            foreach (var target in targets) {
                if (!IsTarget(target, owner)) continue;
                Vector3 center = target.GetCenterPoint();
                if (Vector3.Distance(center, point) > radius + target.GetRadius()) continue;
                if (Physics.Linecast(point + normal * 0.15f, center, out RaycastHit obstruction, WorldMask, QueryTriggerInteraction.Ignore)) {
                    plugin.InfoLog("BOSSMAGIC_OCCLUDED target=" + target.name + " center=" + center + " obstacle=" + obstruction.collider.name); continue;
                }
                Vector3 dir = center - point; dir.y = 0.12f;
                var hit = new HitData { m_point = center, m_dir = dir.normalized, m_pushForce = push,
                    m_dodgeable = true, m_blockable = true, m_ranged = true, m_staggerMultiplier = 0f };
                hit.m_damage.m_lightning = damage;
                hit.SetAttacker(owner);
                target.Damage(hit);
                count++; DamageCount++;
            }
            NetworkMagic.Impact(owner,point + normal * .08f,radius);
            LightningVisual.Burst(point + normal * 0.08f, radius);
            ImpactEffects.Play(point + normal * 0.08f, radius);
            if (GameCamera.instance) GameCamera.instance.AddShake(point, 0.45f, 0.12f, false);
            plugin.InfoLog("BOSSMAGIC_IMPACT targets=" + count + " electrical=" + damage + " push=" + push + " point=" + point);
        }

        internal static Material GetGlow()
        {
            if (!Glow) {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (!shader) throw new InvalidOperationException("No supported lightning shader found.");
                Glow = new Material(shader) { name = "BossMagic_ElectricGlow", color = new Color(0.25f, 0.8f, 1f, 1f) };
            }
            return Glow;
        }

        internal static ItemDrop.ItemData GiveRune(Player player)
        {
            var inventory = player.GetInventory();
            var item = PrefabManager.Instance.GetPrefab(RuneName).GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = PrefabManager.Instance.GetPrefab(RuneName);
            item.m_stack = 1;
            if (!inventory.AddItem(item)) { Say(player, "Inventario cheio."); return null; }
            return item;
        }

        private sealed class RuneCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_rune";
            public override string Help => "Entrega uma runa eletrica de teste. Coloque-a na hotbar e use sua tecla.";
            public override void Run(string[] args) {
                var player = Player.m_localPlayer;
                if (!player || !ZNet.instance) return;
                if (GiveRune(player) != null) Say(player, "Runa recebida. Arraste para a hotbar.");
            }
        }
    }
}




