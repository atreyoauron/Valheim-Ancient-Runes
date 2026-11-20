using System;
using System.Linq;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
namespace BossMagic.Test
{
    internal static class BeaconEffects
    {
        private static GameObject holder, template;
        internal static int ParticleCount, AudioCount, Plays;
        internal static bool Safe;
        internal static GameObject LastEffect;
        internal static void Prepare()
        {
            if (template) return;
            var incinerator = PrefabManager.Instance.GetPrefab("incinerator").GetComponent<Incinerator>();
            var source = (GameObject)AccessTools.Field(typeof(Incinerator), "m_lightingAOEs").GetValue(incinerator);
            if (!source) throw new InvalidOperationException("Obliterator lightning visual missing");
            holder = new GameObject("BossMagic_BeaconTemplates"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            template = UnityEngine.Object.Instantiate(source, holder.transform, false);
            template.name = "BossMagic_BeaconVisual";
            foreach (var script in template.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(script is ZSFX) && !(script is LightFlicker)) UnityEngine.Object.DestroyImmediate(script);
            foreach (var collider in template.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var body in template.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            foreach (var particle in template.GetComponentsInChildren<ParticleSystem>(true)) {
                var main = particle.main; main.loop = false; main.startDelay = 0; main.stopAction = ParticleSystemStopAction.None;
                Plugin.instance.InfoLog("BOSSMAGIC_BEACON_PARTICLE name=" + particle.name + " local=" + particle.transform.localPosition + " size=" + main.startSize.constantMax);
            }
            foreach (var audio in template.GetComponentsInChildren<ZSFX>(true)) {
                audio.m_minVol *= .65f; audio.m_maxVol *= .65f; audio.m_minDelay = audio.m_maxDelay = 0; audio.m_useVibration = false;
            }
            template.SetActive(false);
            ParticleCount = template.GetComponentsInChildren<ParticleSystem>(true).Length;
            AudioCount = template.GetComponentsInChildren<ZSFX>(true).Sum(s => s.m_audioClips.Count(c => c));
            Safe = template.GetComponentsInChildren<Aoe>(true).Length == 0 && template.GetComponentsInChildren<ZNetView>(true).Length == 0 && template.GetComponentsInChildren<Collider>(true).Length == 0;
            Plugin.instance.InfoLog("BOSSMAGIC_BEACON_EFFECT source=" + source.name + " particles=" + ParticleCount + " audio=" + AudioCount + " safe=" + Safe);
        }
        internal static void Play(Vector3 point)
        {
            Prepare();
            LastEffect = UnityEngine.Object.Instantiate(template, point, Quaternion.identity);
            LastEffect.SetActive(true); Plays++;
            UnityEngine.Object.Destroy(LastEffect, 8);
        }
    }
}
