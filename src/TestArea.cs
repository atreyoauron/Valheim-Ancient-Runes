using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
namespace BossMagic.Test
{
    internal static class TestArea
    {
        internal const string SubjectKey = "BossMagic.TestSubject.v1";
        internal const string FrozenKey = "BossMagic.TestSubjectFrozen.v1";
        private static string legacyWorld;
        internal static void Initialize(Harmony harmony)
        {
            var path = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "evidence", "test-world.txt"));
            legacyWorld = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            CommandManager.Instance.AddConsoleCommand(new ResetCommand());
            CommandManager.Instance.AddConsoleCommand(new SpawnCommand());
            harmony.Patch(AccessTools.Method(typeof(MonsterAI), "UpdateAI"), prefix: new HarmonyMethod(typeof(TestArea), nameof(UpdateAI)));
        }
        private static bool Local => Player.m_localPlayer && ZNet.instance && ZNet.instance.IsServer() && ZNet.instance.GetPeerConnections() == 0;
        private static ZDO Data(Character subject) => subject ? subject.GetComponent<ZNetView>()?.GetZDO() : null;
        internal static bool IsLegacy(Character subject) => subject && !(subject is Player) &&
            !string.IsNullOrEmpty(legacyWorld) && ZNet.instance && ZNet.instance.GetWorldName() == legacyWorld &&
            subject.name.Replace("(Clone)", "") == "Greydwarf" && Mathf.Abs(subject.GetMaxHealth() - 1000) < .1f;
        internal static bool IsSubject(Character subject) => subject && !(subject is Player) && (Data(subject)?.GetBool(SubjectKey, false) == true || IsLegacy(subject));
        private static bool UpdateAI(MonsterAI __instance, ref bool __result)
        {
            if (!Local) return true;
            var character = __instance.GetComponent<Character>();
            if (Data(character)?.GetBool(FrozenKey, false) == true || (IsLegacy(character) && character.IsTamed())) { __result = false; return false; }
            return true;
        }
        internal static Character Spawn(Vector3 point, bool ally, bool frozen)
        {
            var obj = UnityEngine.Object.Instantiate(PrefabManager.Instance.GetPrefab("Greydwarf"), point, Quaternion.identity);
            var character = obj.GetComponent<Character>();
            Data(character).Set(SubjectKey, true); Data(character).Set(FrozenKey, frozen || ally);
            character.SetMaxHealth(1000); character.SetHealth(1000); character.SetTamed(ally);
            if (frozen || ally) foreach (var ai in obj.GetComponents<BaseAI>()) ai.enabled = false;
            return character;
        }
        internal static int Reset(Vector3 center, float radius)
        {
            if (!Local) return 0;
            int count = 0;
            foreach (var subject in Character.GetAllCharacters().ToArray()) {
                if (!IsSubject(subject) || Vector3.Distance(center, subject.transform.position) > radius) continue;
                var view = subject.GetComponent<ZNetView>();
                if (!view || !view.IsValid()) continue;
                view.ClaimOwnership(); ZNetScene.instance.Destroy(subject.gameObject); count++;
            }
            Plugin.instance.InfoLog("BOSSMAGIC_AREA_RESET removed=" + count + " radius=" + radius);
            return count;
        }
        private sealed class ResetCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_reset";
            public override string Help => "Remove alvos de teste carregados em 100m, sem loot. Opcional: raio de 10 a 300m. Nao remove criaturas comuns.";
            public override void Run(string[] args)
            {
                if (!Local) return;
                float radius = 100;
                if (args.Length > 1 || (args.Length == 1 && (!float.TryParse(args[0], out radius) || float.IsNaN(radius) || radius < 10 || radius > 300))) { MagicSystem.Say(Player.m_localPlayer, "Uso: bossmagic_reset [raio de 10 a 300]"); return; }
                int count = Reset(Player.m_localPlayer.transform.position, radius);
                MagicSystem.Say(Player.m_localPlayer, "Area limpa: " + count + " alvos de teste removidos.");
            }
        }
        private sealed class SpawnCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_targets";
            public override string Help => "Cria alvos marcados. Uso: bossmagic_targets [1-10] [enemy|ally]. Padrao: 3 inimigos; aliados ficam passivos.";
            public override void Run(string[] args)
            {
                if (!Local) return;
                int count = 3;
                if (args.Length > 2 || (args.Length > 0 && (!int.TryParse(args[0], out count) || count < 1 || count > 10)) || (args.Length == 2 && args[1] != "enemy" && args[1] != "ally")) { MagicSystem.Say(Player.m_localPlayer, "Uso: bossmagic_targets [1-10] [enemy|ally]"); return; }
                bool ally = args.Length == 2 && args[1] == "ally";
                var player = Player.m_localPlayer; var forward = Vector3.ProjectOnPlane(player.GetLookDir(), Vector3.up).normalized;
                if (forward.sqrMagnitude < .1f) forward = Vector3.forward;
                var right = Vector3.Cross(Vector3.up, forward);
                for (int i = 0; i < count; i++) {
                    var point = player.transform.position + forward * (7 + i / 4 * 3) + right * ((i % 4 - (Mathf.Min(count, 4) - 1) / 2f) * 2);
                    if (Physics.Raycast(point + Vector3.up * 4, Vector3.down, out var ground, 15, MagicSystem.WorldMask, QueryTriggerInteraction.Ignore)) Spawn(ground.point + Vector3.up * .2f, ally, ally);
                }
                MagicSystem.Say(player, ally ? "Alvos aliados passivos criados." : "Inimigos de teste criados.");
            }
        }
    }
}
