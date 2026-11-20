using System;
using System.Linq;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;

namespace BossMagic.Test
{
    internal static class ImpactEffects
    {
        private static GameObject holder, stomp, sledge;
        internal static int Plays;
        internal static int SparkSystems, ThunderClips, SledgeSystems;
        internal static bool EffectsSafe;
        private static float nativeRadius = 5f;

        internal static void Prepare()
        {
            if (holder) return;
            var boss = PrefabManager.Instance.GetPrefab("Eikthyr").GetComponent<Humanoid>();
            var weapons = HarmonyLib.AccessTools.Field(typeof(Humanoid), "m_defaultItems").GetValue(boss) as GameObject[];
            var weapon = weapons.First(w => w.name == "Eikthyr_stomp").GetComponent<ItemDrop>();
            var original = weapon.m_itemData.m_shared.m_triggerEffect.m_effectPrefabs.First(e => e.m_prefab && e.m_prefab.name == "fx_eikthyr_stomp").m_prefab;
            var hammer = PrefabManager.Instance.GetPrefab("SledgeIron").GetComponent<ItemDrop>();
            var wave = hammer.m_itemData.m_shared.m_triggerEffect.m_effectPrefabs.First(e => e.m_prefab && e.m_prefab.name == "vfx_sledge_iron_hit").m_prefab;
            Plugin.instance.InfoLog("BOSSMAGIC_STOMP_COMPONENTS " + string.Join(",", original.GetComponentsInChildren<Component>(true).Where(c => c).Select(c => c.GetType().Name).Distinct()));
            foreach (var effect in original.GetComponentsInChildren<ZSFX>(true))
                Plugin.instance.InfoLog("BOSSMAGIC_STOMP_AUDIO " + effect.name + " clips=" + string.Join(",", effect.m_audioClips.Where(c => c).Select(c => c.name)));
            var aoe = original.GetComponentInChildren<Aoe>(true);
            if (aoe) {
                var field = HarmonyLib.AccessTools.Field(typeof(Aoe), "m_radius");
                if (field != null) nativeRadius = Mathf.Max(1f, (float)field.GetValue(aoe));
                DumpEffects("fx_eikthyr_stomp.aoe", aoe);
            }
            holder = new GameObject("BossMagic_VisualTemplates"); holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            stomp = VisualOnly(original, "BossMagic_StompVisual");
            sledge = VisualOnly(wave, "BossMagic_SledgeVisual");
            SparkSystems = stomp.GetComponentsInChildren<ParticleSystem>(true).Length;
            ThunderClips = stomp.GetComponentsInChildren<ZSFX>(true).Sum(s => s.m_audioClips.Count(c => c));
            SledgeSystems = sledge.GetComponentsInChildren<ParticleSystem>(true).Length;
            EffectsSafe = stomp.GetComponentsInChildren<Aoe>(true).Length == 0 && sledge.GetComponentsInChildren<Aoe>(true).Length == 0 &&
                stomp.GetComponentsInChildren<Collider>(true).Length == 0 && sledge.GetComponentsInChildren<Collider>(true).Length == 0;
            Plugin.instance.InfoLog("BOSSMAGIC_IMPACT_ASSETS sparks=" + SparkSystems + " thunderClips=" + ThunderClips + " sledge=" + SledgeSystems + " visualOnly=" + EffectsSafe + " nativeRadius=" + nativeRadius);
        }

        private static GameObject VisualOnly(GameObject source, string name)
        {
            // Instantiate under an inactive parent so gameplay Awake/OnEnable never runs.
            var template = UnityEngine.Object.Instantiate(source, holder.transform, false);
            template.name = name;
            foreach (var behaviour in template.GetComponentsInChildren<MonoBehaviour>(true)) {
                if (behaviour is ZSFX || behaviour is LightFlicker) continue;
                UnityEngine.Object.DestroyImmediate(behaviour);
            }
            foreach (var collider in template.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var body in template.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            foreach (var sfx in template.GetComponentsInChildren<ZSFX>(true)) {
                sfx.m_minVol *= 0.65f; sfx.m_maxVol *= 0.65f; sfx.m_useVibration = false;
            }
            template.SetActive(false);
            return template;
        }

        internal static void Play(Vector3 position, float radius)
        {
            Prepare();
            Spawn(stomp, position, Mathf.Clamp(radius / nativeRadius, 0.15f, 1f));
            Spawn(sledge, position, Mathf.Clamp(radius / 4f, 0.15f, 1.5f));
            Plays++;
        }
        private static void Spawn(GameObject template, Vector3 position, float scale)
        {
            var effect = UnityEngine.Object.Instantiate(template, position, Quaternion.identity);
            effect.transform.localScale *= scale;
            foreach (var particle in effect.GetComponentsInChildren<ParticleSystem>(true)) {
                var main = particle.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            effect.SetActive(true);
            UnityEngine.Object.Destroy(effect, 8f);
        }

        internal static void InspectAssets()
        {
            foreach (string name in new[] { "Eikthyr", "eikthyr_stomp", "SledgeIron", "SledgeStagbreaker" }) {
                var prefab = PrefabManager.Instance.GetPrefab(name);
                if (!prefab) { Plugin.instance.InfoLog("BOSSMAGIC_ASSET_MISSING " + name); continue; }
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop) {
                    DumpEffects(name + ".shared", drop.m_itemData.m_shared);
                    DumpEffects(name + ".attack", drop.m_itemData.m_shared.m_attack);
                }
                var humanoid = prefab.GetComponent<Humanoid>();
                if (humanoid) {
                    var items = HarmonyLib.AccessTools.Field(typeof(Humanoid), "m_defaultItems")?.GetValue(humanoid) as GameObject[];
                    if (items != null) foreach (var item in items) {
                        Plugin.instance.InfoLog("BOSSMAGIC_BOSS_WEAPON " + item.name);
                        var weapon = item.GetComponent<ItemDrop>();
                        if (weapon) { DumpEffects(item.name + ".shared", weapon.m_itemData.m_shared); DumpEffects(item.name + ".attack", weapon.m_itemData.m_shared.m_attack); }
                    }
                }
            }
            foreach (var obj in Resources.FindObjectsOfTypeAll<GameObject>().Where(o => o.name.StartsWith("sfx_eikthyr", StringComparison.OrdinalIgnoreCase) || o.name.StartsWith("vfx_eikthyr", StringComparison.OrdinalIgnoreCase) || o.name.StartsWith("vfx_sledge", StringComparison.OrdinalIgnoreCase))) {
                Plugin.instance.InfoLog("BOSSMAGIC_EFFECT_ASSET " + obj.name + " components=" + string.Join(",", obj.GetComponentsInChildren<Component>(true).Where(c => c).Select(c => c.GetType().Name).Distinct()));
            }
        }
        private static void DumpEffects(string label, object data)
        {
            foreach (var field in data.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                if (field.FieldType != typeof(EffectList)) continue;
                var effects = field.GetValue(data) as EffectList;
                if (effects?.m_effectPrefabs == null) continue;
                foreach (var effect in effects.m_effectPrefabs) if (effect.m_prefab) Plugin.instance.InfoLog("BOSSMAGIC_EFFECT_REFERENCE " + label + "." + field.Name + "=" + effect.m_prefab.name);
            }
        }
    }
}

