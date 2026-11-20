using System;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace BossMagic.Test
{
    [BepInPlugin("local.bossmagic.test", "Valheim - Ancient Runes", "0.13.0")]
    [BepInDependency("com.jotunn.jotunn", BepInDependency.DependencyFlags.HardDependency)]
    [Jotunn.Utils.NetworkCompatibility(Jotunn.Utils.CompatibilityLevel.EveryoneMustHaveMod, Jotunn.Utils.VersionStrictness.Patch)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin instance;
        private static string saveRoot;
        internal static bool IsLab;
        private bool smokeTest;
        private bool menuReady;
        private float elapsed;
        private float menuElapsed;

        private void Awake()
        {
            instance = this;
            try
            {
                string labRoot = Path.GetFullPath(Path.Combine(Paths.GameRootPath, ".."));
                saveRoot = Path.Combine(labRoot, "saves");
                string[] args = Environment.GetCommandLineArgs();
                int index = Array.IndexOf(args, "-savedir");
                IsLab = File.Exists(Path.Combine(labRoot, "lab.marker"));
                if (IsLab && (index < 0 ||
                    index + 1 >= args.Length || !string.Equals(Path.GetFullPath(args[index + 1]), saveRoot, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Use Start-Lab.ps1: isolated save directory is required.");

                smokeTest = Array.IndexOf(args, "-bossmagic-smoke") >= 0;
                var harmony = new Harmony("local.bossmagic.test.isolation");
                if (IsLab) {
                Patch(harmony, AccessTools.PropertyGetter(typeof(FileHelpers), "CloudStorageSupported"), nameof(NoCloud));
                Patch(harmony, AccessTools.PropertyGetter(typeof(FileHelpers), "CloudStorageSupportedAndEnabled"), nameof(NoCloud));
                Patch(harmony, AccessTools.Method(typeof(Utils), "GetSaveDataPath"), nameof(LocalSavePath));
                Utils.SetSaveDataPath(saveRoot);
                }
                var menuStart = AccessTools.Method(AccessTools.TypeByName("FejdStartup"), "Start");
                if (menuStart != null) harmony.Patch(menuStart, postfix: new HarmonyMethod(typeof(Plugin), nameof(MenuStarted)));
                else if (IsLab) throw new MissingMethodException("FejdStartup.Start");
                MagicSystem.Initialize(this, harmony);
                SurgeSystem.Initialize(this, harmony);
                BeaconSystem.Initialize(this, harmony);
                WindScar.Initialize(this, harmony);
                LightningChain.Initialize(this, harmony);
                RootCage.Initialize(this, harmony);
                VineSwarm.Initialize(this, harmony); NatureBlessing.Initialize(this, harmony);
                RuneBag.Initialize(this, harmony);
                if (IsLab) TestArea.Initialize(harmony);
                RuneHotbar.Initialize(this, harmony);
                NetworkMagic.Initialize(harmony);
                Logger.LogInfo("BOSSMAGIC_TEST_LOADED version=0.13.0");
                Logger.LogInfo(IsLab ? "BOSSMAGIC_ISOLATION_OK cloud=false saves=" + saveRoot : "ANCIENT_RUNES_MULTIPLAYER experimental; normal game save paths preserved");
                Logger.LogInfo("BOSSMAGIC_PLUGIN_PATH " + Info.Location);
            }
            catch (Exception error)
            {
                Logger.LogError("BOSSMAGIC_ISOLATION_FAILED " + error);
                Application.Quit(1);
                enabled = false;
            }
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefix)
        {
            if (target == null) throw new MissingMethodException(prefix);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), prefix));
        }

        private static bool NoCloud(ref bool __result) { __result = false; return false; }
        private static bool LocalSavePath(ref string __result) { __result = saveRoot; return false; }
        private static void MenuStarted()
        {
            instance.menuReady = true;
            if (IsLab) GameplayTest.OnMenu(instance);
            instance.Logger.LogInfo("BOSSMAGIC_MENU_READY saves=" + Utils.GetSaveDataPath(FileHelpers.FileSource.Local));
        }

        internal void InfoLog(string message) => Logger.LogInfo(message);
        internal void ErrorLog(string message) => Logger.LogError(message);

        private void Update()
        {
            NetworkMagic.Tick();
            SurgeSystem.Tick();
            LightningChain.Tick();
            RootCage.Tick();
            VineSwarm.Tick();
            RuneBag.Tick();
            RuneHotbar.Tick();
            if (!smokeTest) return;
            elapsed += Time.unscaledDeltaTime;
            if (menuReady) menuElapsed += Time.unscaledDeltaTime;
            if (menuElapsed > 5f && MagicSystem.RuneReady && SurgeSystem.Ready && RuneBag.Ready && BeaconSystem.Ready && WindScar.Ready && LightningChain.Ready && RootCage.Ready && VineSwarm.Ready && NatureBlessing.Ready)
            {
                Logger.LogInfo("BOSSMAGIC_SMOKE_PASS: plugin and menu initialized; no world entered.");
                enabled = false;
                Application.Quit(0);
            }
            else if (elapsed > 120f)
            {
                Logger.LogError("BOSSMAGIC_SMOKE_TIMEOUT: main menu did not initialize.");
                enabled = false;
                Application.Quit(2);
            }
        }
    }
}

















