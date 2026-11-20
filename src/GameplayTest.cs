using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;

namespace BossMagic.Test
{
    // Opt-in integration test. It only creates fresh saves inside the guarded laboratory.
    internal static class GameplayTest
    {
        private static bool started;
        private static Plugin plugin;
        private static readonly System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,object> savedConfig=new System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,object>();
        private static bool savedAutoSave;
        private static void RestoreTestConfig() { foreach(var pair in savedConfig) pair.Key.BoxedValue=pair.Value; savedConfig.Clear(); plugin.Config.SaveOnConfigSet=savedAutoSave; }
        private static readonly System.Collections.Generic.List<GameObject> woodFixtures = new System.Collections.Generic.List<GameObject>();
        private static void ClearWoodFixtures() { foreach (var obj in woodFixtures) if (obj && ZNetScene.instance) ZNetScene.instance.Destroy(obj); woodFixtures.Clear(); }
        internal static void OnMenu(Plugin host)
        {
            if (started || Array.IndexOf(Environment.GetCommandLineArgs(), "-bossmagic-gameplay-test") < 0) return;
            started = true; plugin = host;
            savedAutoSave=host.Config.SaveOnConfigSet; host.Config.SaveOnConfigSet=false;
            foreach(var pair in host.Config) { savedConfig[pair.Value]=pair.Value.BoxedValue; pair.Value.BoxedValue=pair.Value.DefaultValue; }
            host.StartCoroutine(Guard(Run()));
        }
        private static IEnumerator Guard(IEnumerator test)
        {
            float start = Time.realtimeSinceStartup;
            while (true) {
                object step;
                try {
                    if (Time.realtimeSinceStartup - start > 240f) throw new TimeoutException("Gameplay test timed out.");
                    if (!test.MoveNext()) break;
                    step = test.Current;
                } catch (Exception e) {
                    plugin.ErrorLog("BOSSMAGIC_GAMEPLAY_FAIL " + e);
                    ClearWoodFixtures();
                    if (Player.m_localPlayer) TestArea.Reset(Player.m_localPlayer.transform.position, 300);
                    RestoreTestConfig();
                    Application.Quit(3); yield break;
                }
                yield return step;
            }
        }
        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            plugin.InfoLog("BOSSMAGIC_ASSERT_PASS " + message);
        }
        private static ItemDrop.ItemData Add(Player player, string prefabName, int slot)
        {
            var prefab = PrefabManager.Instance.GetPrefab(prefabName);
            if (!prefab) throw new InvalidOperationException("Missing " + prefabName);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = 1;
            if (!player.GetInventory().AddItem(item)) throw new InvalidOperationException("Inventory full");
            item.m_gridPos = new Vector2i(slot, 0);
            return item;
        }
        private static Character Target(Vector3 point, bool tamed)
        {
            return TestArea.Spawn(point, tamed, true);
        }
        private static void Aim(Player player, Vector3 target)
        {
            Vector3 cameraPoint = player.GetEyePoint() - Vector3.forward * 3f + Vector3.up * 2f;
            Camera.main.transform.position = cameraPoint;
            Camera.main.transform.rotation = Quaternion.LookRotation(target - cameraPoint);
            player.SetLookDir((target - player.GetEyePoint()).normalized, 0f);
        }
        private static void CaptureView(string filename = "lightning-cast.png")
        {
            var camera = Camera.main;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var target = RenderTexture.GetTemporary(1280, 720, 24);
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
                File.WriteAllBytes(Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "evidence", filename)), pixels.EncodeToPNG());
            } finally {
                camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(pixels);
            }
        }

        private static IEnumerator RunNature(Player player)
        {
            float stable=0; while(stable<3f) { stable=player.InCutscene()||player.IsTeleporting()?0:stable+Time.deltaTime; yield return null; }
            NatureBlessing.Prepare();
            Require(NatureBlessing.Native.name=="SE_Greydwarf_shaman_heal","native Shaman healing source resolved");
            player.GetInventory().RemoveAll(); var sword=Add(player,"SwordBronze",0);player.EquipItem(sword,false);
            var rune=Add(player,NatureBlessing.RuneName,1);
            Require(RuneBag.Accepts(rune),"Nature rune accepted by default bag");
            var ally=Target(player.transform.position+Vector3.right*2,true);
            var enemy=Target(player.transform.position+Vector3.left*2,false);
            var far=Target(player.transform.position+Vector3.forward*10,true);
            yield return new WaitForSeconds(.3f);
            player.SetHealth(5);ally.SetHealth(5);enemy.SetHealth(5);far.SetHealth(5);
            player.AddStamina(200);NatureBlessing.NextCast=0;
            Cursor.visible=false;Cursor.lockState=CursorLockMode.Locked;
            plugin.InfoLog("NATURE_STATE dead="+player.IsDead()+" attack="+player.InAttack()+" teleport="+player.IsTeleporting()+" cutscene="+player.InCutscene()+" attached="+player.IsAttached()+" stagger="+player.IsStaggering()+" dodge="+player.IsDodgeInvincible()+" slot="+player.GetInventory().GetItemAt(1,0)?.m_dropPrefab?.name);
            float costBefore=player.GetStamina();
            AccessTools.Method(typeof(Player),"UseHotbarItem").Invoke(player,new object[]{2});
            Require(NatureBlessing.NextCast>Time.time,"Nature rune activates from normal hotbar");
            Require(Mathf.Abs(player.GetStamina()-(costBefore-NatureBlessing.Cost.Value))<.1f,"Nature charges activation stamina");
            Require(player.GetHealth()==5 && ally.GetHealth()==5,"Nature healing is not instantaneous");
            int hash="BossMagic_NatureBlessing".GetStableHashCode();
            Require(player.GetSEMan().GetStatusEffect(hash)&&ally.GetSEMan().GetStatusEffect(hash),"Nature applies to caster and nearby tame");
            Require(!enemy.GetSEMan().GetStatusEffect(hash)&&!far.GetSEMan().GetStatusEffect(hash),"Nature excludes enemies and allies outside radius");
            Require(!NatureBlessing.TryCast(player),"Nature cooldown blocks repeated cast");
            yield return new WaitForSeconds(1.3f);
            Require(player.GetHealth()>5&&ally.GetHealth()>5&&ally.GetHealth()<25,"Nature restores health gradually");
            RuneHotbarTest.Capture("blessing-of-nature.png");
            yield return new WaitForSeconds(4f);
            Require(!player.GetSEMan().GetStatusEffect(hash)&&!ally.GetSEMan().GetStatusEffect(hash),"Nature expires after configured duration");
            Require(ally.GetHealth()>=24.9f&&ally.GetHealth()<25.2f,"Nature delivers configured total healing");
            Require(enemy.GetHealth()==5&&far.GetHealth()==5,"Nature does not heal excluded creatures");
            Require(AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player)==sword,"Nature preserves equipped weapon");
            Require(NatureBlessing.Native.m_healthOverTime==20&&NatureBlessing.Native.m_ttl==4,"Native Shaman settings remain unchanged");
            plugin.InfoLog("BOSSMAGIC_NATURE_PASS");
        }
        private static IEnumerator Run()
        {
            Application.runInBackground = true;
            while (!MagicSystem.RuneReady) yield return null;
            yield return new WaitForSeconds(3f);
            string name = "MagicTest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var profile = new PlayerProfile(name, FileHelpers.FileSource.Local);
            profile.SetName("Teste de Runas");
            AccessTools.Field(typeof(PlayerProfile), "m_firstSpawn").SetValue(profile, false);
            Require(profile.Save(), "fresh local profile saved");
            Game.SetProfile(name, FileHelpers.FileSource.Local);
            string marker = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "..", "evidence", "test-world.txt"));
            string worldName = File.Exists(marker) ? File.ReadAllText(marker).Trim() : name;
            Require(worldName.StartsWith("MagicTest_", StringComparison.Ordinal), "integration world has reserved name");
            File.WriteAllText(marker, worldName);
            var world = World.GetCreateWorld(worldName, FileHelpers.FileSource.Local);
            ZNet.SetServer(true, false, false, name, "", world);
            ZNet.ResetServerHost();
            var startup = UnityEngine.Object.FindFirstObjectByType<FejdStartup>();
            AccessTools.Method(typeof(FejdStartup), "LoadMainScene").Invoke(startup, null);
            plugin.InfoLog("BOSSMAGIC_GAMEPLAY_WORLD " + name);
            while (!Player.m_localPlayer) yield return null;
            var player = Player.m_localPlayer;
            while (player.InCutscene() || player.IsTeleporting()) yield return null;
            yield return new WaitForSeconds(2f);
            player.SetGodMode(true);
            TestArea.Reset(player.transform.position, 300);
            if(Array.IndexOf(Environment.GetCommandLineArgs(),"-bossmagic-nature-test")>=0) {
                var natureTest=RunNature(player); while(natureTest.MoveNext()) yield return natureTest.Current;
                TestArea.Reset(player.transform.position,300); RestoreTestConfig();
                plugin.InfoLog("BOSSMAGIC_GAMEPLAY_PASS: focused Blessing of Nature test only."); Application.Quit(0); yield break;
            }
            if(Array.IndexOf(Environment.GetCommandLineArgs(),"-bossmagic-network-test")>=0) {
                var netTest=NetworkTest.Run(player);while(netTest.MoveNext())yield return netTest.Current;
                RestoreTestConfig();plugin.InfoLog("BOSSMAGIC_GAMEPLAY_PASS: network receiver test");Application.Quit(0);yield break;
            }
            BeaconEffects.Prepare();
            ImpactEffects.Prepare();
            Require(ImpactEffects.SparkSystems > 0 && ImpactEffects.ThunderClips > 0 && ImpactEffects.SledgeSystems > 0, "native stomp thunder, lightning particles and sledge wave resolved");
            Require(ImpactEffects.EffectsSafe, "native visual effects have no damage scripts or colliders");
            // Temporary elevated platform: no terrain edits or existing structures touched.
            var arena = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arena.name = "BossMagic_TestPlatform";
            arena.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Sprites/Default")) { color=new Color(.18f,.22f,.18f,1), renderQueue=2000 };
            arena.layer = LayerMask.NameToLayer("static_solid");
            Vector3 origin = player.transform.position + Vector3.up * 30f;
            arena.transform.position = origin - Vector3.up;
            arena.transform.localScale = new Vector3(24f, 1f, 24f);
            while (!player.TeleportTo(origin, Quaternion.identity, true)) yield return new WaitForSeconds(0.25f);
            while (player.IsTeleporting()) yield return null;
            yield return new WaitForSeconds(1f);
            Require(Vector3.Distance(player.transform.position, origin) < 2f, "player reached integration platform");
            Vector3 probeStart = player.transform.position + Vector3.up * 60f;
            var straightProbe = new GameObject("StraightProbe").AddComponent<LightningMissile>();
            var arrowProbe = new GameObject("ArrowProbe").AddComponent<LightningMissile>();
            straightProbe.transform.position = probeStart;
            arrowProbe.transform.position = probeStart + Vector3.forward * 4f;
            straightProbe.Initialize(player, Vector3.right, MagicSystem.TrajectoryMode.Straight);
            arrowProbe.Initialize(player, Vector3.right, MagicSystem.TrajectoryMode.Arrow);
            yield return new WaitForSeconds(0.5f);
            Require(straightProbe && Mathf.Abs(straightProbe.transform.position.y - probeStart.y) < 0.01f, "Straight mode preserves height for a horizontal shot");
            float expectedDrop = 0.5f * MagicSystem.Gravity.Value * arrowProbe.FlightTime * arrowProbe.FlightTime;
            float actualDrop = probeStart.y - arrowProbe.transform.position.y;
            Require(Mathf.Abs(actualDrop - expectedDrop) < 0.02f, "Arrow mode follows configured gravity");
            plugin.InfoLog("BOSSMAGIC_TRAJECTORY_TEST straightDrop=" + (probeStart.y - straightProbe.transform.position.y) + " arrowDrop=" + actualDrop + " expected=" + expectedDrop);
            UnityEngine.Object.Destroy(straightProbe.gameObject); UnityEngine.Object.Destroy(arrowProbe.gameObject);
            player.GetInventory().RemoveAll();
            var sword = Add(player, "SwordBronze", 0);
            var shield = Add(player, "ShieldWood", 1);
            var rune = Add(player, MagicSystem.RuneName, 2);
            player.EquipItem(sword, false); player.EquipItem(shield, false);
            Require(AccessTools.Field(typeof(Humanoid), "m_rightItem").GetValue(player) == sword && AccessTools.Field(typeof(Humanoid), "m_leftItem").GetValue(player) == shield, "sword and shield equipped");
            Vector3 impactPoint = origin + Vector3.forward * 7f - Vector3.up * 0.45f;
            var enemy = Target(impactPoint + Vector3.right * 0.9f + Vector3.up * 0.2f, false);
            var tame = Target(impactPoint - Vector3.right * 1.2f + Vector3.up * 0.2f, true);
            yield return new WaitForSeconds(1f);
            Require(MagicSystem.IsTarget(enemy, player), "hostile target recognized");
            Require(!MagicSystem.IsTarget(tame, player), "tamed target excluded");
            var useHotbar = AccessTools.Method(typeof(Player), "UseHotbarItem");
            player.AddStamina(100f);
            float stamina = player.GetStamina(), health = enemy.GetHealth(), tameHealth = tame.GetHealth();
            int casts = MagicSystem.CastCount, impacts = MagicSystem.ImpactCount, hits = MagicSystem.DamageCount;
            Vector3 enemyStart = enemy.transform.position;
            plugin.InfoLog("BOSSMAGIC_ARENA origin=" + origin + " player=" + player.transform.position + " target=" + enemy.GetCenterPoint() + " aim=" + impactPoint);
            plugin.InfoLog("BOSSMAGIC_TARGET mass=" + enemy.GetComponent<Rigidbody>().mass + " sleeping=" + enemy.GetBaseAI().IsSleeping());
            Aim(player, impactPoint);
            useHotbar.Invoke(player, new object[] { 3 });
            Require(MagicSystem.CastCount == casts + 1, "hotbar slot 3 casts rune");
            Require(AccessTools.Field(typeof(Humanoid), "m_rightItem").GetValue(player) == sword && AccessTools.Field(typeof(Humanoid), "m_leftItem").GetValue(player) == shield, "casting preserves both equipped weapons");
            Require(player.GetInventory().ContainsItem(rune) && rune.m_stack == 1, "rune is reusable and not consumed");
            Require(Mathf.Abs(stamina - player.GetStamina() - MagicSystem.Cost.Value) < 0.1f, "stamina cost applied exactly once");
            useHotbar.Invoke(player, new object[] { 3 });
            Require(MagicSystem.CastCount == casts + 1, "cooldown blocks repeated hotbar use");
            yield return new WaitForSeconds(0.4f);
            CaptureView();
            yield return new WaitForSeconds(0.6f);
            Require(ImpactEffects.Plays > 0, "impact spawns the paired native effects");
            Require(MagicSystem.ImpactCount == impacts + 1, "projectile collides and explodes exactly once");
            Require(MagicSystem.DamageCount == hits + 1, "impact damages hostile target once despite multiple colliders");
            Require(enemy.GetHealth() < health, "electrical hit reduces enemy health");
            Require(Mathf.Abs(tame.GetHealth() - tameHealth) < 0.01f, "tamed creature takes no damage");
            Require(Vector3.ProjectOnPlane(enemy.transform.position - enemyStart, Vector3.up).magnitude > 0.05f, "impact moves hostile target");
            plugin.InfoLog("BOSSMAGIC_GAMEPLAY_DAMAGE before=" + health + " after=" + enemy.GetHealth() + " displacement=" + Vector3.Distance(enemy.transform.position, enemyStart));
            MagicSystem.NextCast = 0f;
            player.UseStamina(player.GetStamina() + 10f);
            useHotbar.Invoke(player, new object[] { 3 });
            Require(MagicSystem.CastCount == casts + 1, "insufficient stamina blocks casting");
            // Wall occlusion: explicit impact exercises the same area damage path.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.layer = LayerMask.NameToLayer("static_solid");
            wall.transform.position = origin + Vector3.forward * 3f + Vector3.up;
            wall.transform.localScale = new Vector3(12f, 8f, 0.4f);
            enemy.transform.position = origin + Vector3.forward * 4f;
            tame.transform.position = origin + Vector3.right * 9f;
            Physics.SyncTransforms();
            health = enemy.GetHealth(); hits = MagicSystem.DamageCount;
            MagicSystem.Impact(player, origin + Vector3.forward * 2f, Vector3.up, 25f, 3f, 5f);
            yield return new WaitForSeconds(0.2f);
            Require(MagicSystem.DamageCount == hits && Mathf.Abs(enemy.GetHealth() - health) < 0.01f, "solid wall blocks area damage");
            UnityEngine.Object.Destroy(wall);
            yield return new WaitForSeconds(8f);
            Require(SurgeSystem.Ready, "Surge registered");
            var surge = Add(player, SurgeSystem.RuneName, 3);
            player.AddStamina(200);
            float normalSpeed = (float)AccessTools.Method(typeof(Player), "GetRunSpeedFactor").Invoke(player, null);
            stamina = player.GetStamina();
            useHotbar.Invoke(player, new object[] { 4 });
            Require(SurgeSystem.Active, "Surge activates through hotbar");
            Require(Mathf.Abs(stamina - player.GetStamina() - SurgeSystem.Cost.Value) < .1f, "Surge charges activation once");
            Require(Mathf.Abs((float)AccessTools.Method(typeof(Player), "GetRunSpeedFactor").Invoke(player, null) - normalSpeed * SurgeSystem.Multiplier.Value) < .01f, "Surge increases running speed");
            Require(AccessTools.Field(typeof(Humanoid), "m_rightItem").GetValue(player) == sword && AccessTools.Field(typeof(Humanoid), "m_leftItem").GetValue(player) == shield, "Surge preserves sword and shield");
            Require(player.GetInventory().ContainsItem(surge), "Surge rune remains in inventory");
            float drain = 10;
            player.GetSEMan().ModifyRunStaminaDrain(10, ref drain, Vector3.forward, true);
            Require(drain == 0, "ActivationOnly removes normal sprint drain");
            float charged = player.GetStamina();
            useHotbar.Invoke(player, new object[] { 4 });
            Require(player.GetStamina() == charged, "Surge cannot stack or charge twice");
            Require(SurgeSystem.Aura.GetComponentsInChildren<ParticleSystem>().Length > 0 && SurgeSystem.Aura.GetComponentsInChildren<Collider>().Length == 0, "electrical aura is visual only");
            yield return new WaitForSeconds(1f); CaptureView("surge-native.png");
            yield return new WaitForSeconds(SurgeSystem.Duration.Value + .2f);
            Require(!SurgeSystem.Active && Mathf.Abs((float)AccessTools.Method(typeof(Player), "GetRunSpeedFactor").Invoke(player, null) - normalSpeed) < .01f, "Surge expires and restores speed");
            drain = 10; player.GetSEMan().ModifyRunStaminaDrain(10, ref drain, Vector3.forward, true);
            Require(drain > 0, "normal stamina drain restored");
            var savedMode = SurgeSystem.Mode.Value;
            bool saveConfig = plugin.Config.SaveOnConfigSet;
            plugin.Config.SaveOnConfigSet = false;
            try {
                SurgeSystem.Mode.Value = SurgeSystem.StaminaMode.AdditionalSprintDrain;
                SurgeSystem.NextCast = 0; player.AddStamina(200);
                Require(SurgeSystem.TryCast(player), "additional drain mode activates");
            } finally { SurgeSystem.Mode.Value = savedMode; plugin.Config.SaveOnConfigSet = saveConfig; }
            float extra = 10; player.GetSEMan().ModifyRunStaminaDrain(10, ref extra, Vector3.forward, true);
            Require(Mathf.Abs(extra - drain - SurgeSystem.ExtraDrain.Value) < .01f, "additional drain adds to normal sprint cost");
            player.UseStamina(player.GetStamina() + 10);
            yield return null; yield return null;
            Require(!SurgeSystem.Active, "continuous mode ends when stamina runs out");
            plugin.InfoLog("BOSSMAGIC_SURGE_PASS: hotbar, equipment, activation cost, speed, free sprint, duration, aura, additional drain, depletion, cleanup.");
            Require(BeaconSystem.Ready && BeaconEffects.Safe && BeaconEffects.ParticleCount > 0 && BeaconEffects.AudioCount > 0, "Beacon rune and harmless Obliterator audiovisuals ready");
            var beacon = Add(player, BeaconSystem.RuneName, 7);
            enemy.transform.position = origin + Vector3.forward * 7; enemy.SetHealth(1000);
            tame.transform.position = origin + Vector3.right * 9;
            yield return new WaitForSeconds(.3f);
            Physics.SyncTransforms();
            player.AddStamina(200); Aim(player, enemy.GetCenterPoint());
            Require(BeaconSystem.FindTarget(player, out var beaconReason) == enemy, "Beacon selects enemy directly under crosshair: " + beaconReason);
            health = enemy.GetHealth(); stamina = player.GetStamina(); int beaconCasts = BeaconSystem.CastCount;
            useHotbar.Invoke(player, new object[] { 8 });
            Require(BeaconSystem.CastCount == beaconCasts + 1, "Beacon casts through hotbar");
            Require(Mathf.Abs(health - enemy.GetHealth() - BeaconSystem.Damage.Value) < .1f, "Beacon deals configured pure base damage");
            Require(Mathf.Abs(stamina - player.GetStamina() - BeaconSystem.Cost.Value) < .1f, "Beacon charges stamina once");
            Require(AccessTools.Field(typeof(Humanoid), "m_rightItem").GetValue(player) == sword && AccessTools.Field(typeof(Humanoid), "m_leftItem").GetValue(player) == shield && player.GetInventory().ContainsItem(beacon), "Beacon preserves weapons and reusable rune");
            useHotbar.Invoke(player, new object[] { 8 });
            Require(BeaconSystem.CastCount == beaconCasts + 1, "Beacon cooldown prevents duplicate strike");
            yield return new WaitForSeconds(.08f); CaptureView("beacon-strike.png");
            var modifiersField = AccessTools.Field(typeof(Character), "m_damageModifiers");
            var originalModifiers = (HitData.DamageModifiers)modifiersField.GetValue(enemy);
            var modifiers = originalModifiers; modifiers.m_lightning = HitData.DamageModifier.Immune;
            modifiersField.SetValue(enemy, modifiers);
            enemy.SetHealth(1000); BeaconSystem.ApplyPureHit(player, enemy, 100);
            Require(Mathf.Abs(enemy.GetHealth() - 900) < .1f, "pure hit ignores lightning immunity");
            modifiers.m_lightning = HitData.DamageModifier.VeryWeak; modifiersField.SetValue(enemy, modifiers);
            enemy.SetHealth(1000); BeaconSystem.ApplyPureHit(player, enemy, 100);
            Require(Mathf.Abs(enemy.GetHealth() - 900) < .1f, "pure hit ignores lightning vulnerability");
            var armorProbe = BeaconSystem.MakeHit(player, enemy, 100); armorProbe.ApplyArmor(10000);
            Require(armorProbe.m_damage.m_damage == 100, "pure component survives armor calculation");
            var modifierTemplate = ScriptableObject.CreateInstance<BeaconTestModifier>(); modifierTemplate.name = "BossMagic_BeaconTestReduction";
            modifierTemplate.Factor = .7f; modifierTemplate.m_ttl = 30;
            var reduction = enemy.GetSEMan().AddStatusEffect(modifierTemplate, false, 0, 0, 0);
            Require(reduction != null, "test damage reduction registered through normal status system");
            enemy.SetHealth(1000); BeaconSystem.ApplyPureHit(player, enemy, 100);
            Require(Mathf.Abs(enemy.GetHealth() - 930) < .1f, "100 pure damage respects general 30 percent reduction");
            ((BeaconTestModifier)reduction).Factor = 1.2f;
            enemy.SetHealth(1000); BeaconSystem.ApplyPureHit(player, enemy, 100);
            Require(Mathf.Abs(enemy.GetHealth() - 880) < .1f, "100 pure damage respects general 20 percent amplification");
            ((BeaconTestModifier)reduction).Factor = 0;
            enemy.SetHealth(1000); BeaconSystem.ApplyPureHit(player, enemy, 100);
            Require(Mathf.Abs(enemy.GetHealth() - 1000) < .1f, "general complete damage prevention remains effective");
            enemy.GetSEMan().RemoveStatusEffect(reduction, true); UnityEngine.Object.Destroy(modifierTemplate);
            modifiersField.SetValue(enemy, originalModifiers);
            var untagged = new HitData(100); untagged.ApplyModifier(.7f);
            Require(untagged.m_damage.m_damage == 100, "Beacon modifier patch leaves unrelated generic damage untouched");
            BeaconSystem.NextCast = 0;
            player.UseStamina(player.GetStamina() + 10); Aim(player, enemy.GetCenterPoint());
            Require(!BeaconSystem.TryCast(player), "Beacon cannot cast without stamina");
            player.AddStamina(200); stamina = player.GetStamina();
            Aim(player, player.GetEyePoint() + Vector3.up * 20);
            Require(!BeaconSystem.TryCast(player) && player.GetStamina() == stamina, "missing target does not spend stamina");
            Aim(player, tame.GetCenterPoint());
            Require(!BeaconSystem.TryCast(player), "Beacon refuses tamed creatures");
            var beaconWall = GameObject.CreatePrimitive(PrimitiveType.Cube); beaconWall.layer = LayerMask.NameToLayer("static_solid");
            beaconWall.transform.position = origin + Vector3.forward * 4 + Vector3.up; beaconWall.transform.localScale = new Vector3(8, 8, .5f);
            Physics.SyncTransforms(); Aim(player, enemy.GetCenterPoint());
            Require(!BeaconSystem.TryCast(player), "wall blocks Beacon targeting");
            beaconWall.transform.position = enemy.GetCenterPoint() + Vector3.up * 3; beaconWall.transform.localScale = new Vector3(5, .5f, 5);
            Physics.SyncTransforms(); Aim(player, enemy.GetCenterPoint());
            Require(!BeaconSystem.TryCast(player), "roof blocks incoming Beacon");
            UnityEngine.Object.Destroy(beaconWall);
            Require(RuneBag.Accepts(beacon), "default rune bag accepts Beacon");
            plugin.InfoLog("BOSSMAGIC_BEACON_PASS: native ray, hotbar, equipment, reuse, pure damage, armor, immunity, vulnerability, general modifiers, cooldown, stamina, empty aim, tamed, walls, roof, bag.");
            Require(RuneBag.Ready, "rune bag registered");
            yield return new WaitForSeconds(8f);
            Require(WindScar.Ready, "Wind Scar registered");
            player.GetInventory().RemoveItem(beacon);
            var wind = Add(player, WindScar.RuneName, 7);
            enemy.transform.position = origin + Vector3.forward * 7; enemy.SetHealth(1000);
            Physics.SyncTransforms(); Aim(player, enemy.GetCenterPoint()); player.AddStamina(200);
            int windCasts = WindScar.CastCount, windHits = WindScar.Hits;
            stamina = player.GetStamina(); health = enemy.GetHealth();
            useHotbar.Invoke(player, new object[] { 8 });
            Require(WindScar.CastCount == windCasts + 1 && Mathf.Abs(stamina-player.GetStamina()-WindScar.Cost.Value)<.1f, "Wind Scar hotbar activation charges once");
            Require(AccessTools.Field(typeof(Humanoid), "m_rightItem").GetValue(player) == sword && AccessTools.Field(typeof(Humanoid), "m_leftItem").GetValue(player) == shield && player.GetInventory().ContainsItem(wind), "Wind Scar preserves weapons and reusable rune");
            useHotbar.Invoke(player, new object[] { 8 });
            Require(WindScar.CastCount == windCasts + 1, "Wind Scar cooldown prevents repeat cast");
            yield return new WaitForSeconds(.12f); CaptureView("wind-scar.png");
            yield return new WaitForSeconds(.7f);
            Require(Mathf.Abs(health-enemy.GetHealth()-WindScar.Slash.Value)<.1f && WindScar.Hits==windHits+1, "Wind Scar projectile deals Slash exactly once");
            Vector3 windProbeStart=origin+Vector3.up*60;
            var windProbe=WindScar.Launch(player,windProbeStart,Vector3.right);
            yield return new WaitForSeconds(.4f);
            Require(windProbe && Mathf.Abs(windProbe.transform.position.y-windProbeStart.y)<.01f && windProbe.transform.position.x>windProbeStart.x+1, "Wind Scar flies straight without gravity");
            UnityEngine.Object.Destroy(windProbe.gameObject);
            var treeObject=UnityEngine.Object.Instantiate(PrefabManager.Instance.GetPrefab("Beech1"),origin+Vector3.right*40,Quaternion.identity);
            woodFixtures.Add(treeObject);
            var tree=treeObject.GetComponent<TreeBase>(); var treeData=treeObject.GetComponent<ZNetView>().GetZDO();
            treeData.Set(ZDOVars.s_health,1000f);
            Physics.SyncTransforms();
            Vector3 treeAim=treeObject.transform.position+Vector3.up;
            WindScar.Launch(player,treeAim-Vector3.forward*5,Vector3.forward);
            yield return new WaitForSeconds(.6f);
            Require(Mathf.Abs(treeData.GetFloat(ZDOVars.s_health,1000)- (1000-WindScar.Woodcut.Value))<.1f, "Wind Scar projectile applies high woodcut damage to real tree");
            var treeCollider=tree.GetComponentsInChildren<Collider>().First();
            AccessTools.Field(typeof(TreeBase),"m_minToolTier").SetValue(tree,4);
            float woodHealth=treeData.GetFloat(ZDOVars.s_health,1000);
            WindScar.Impact(player,treeCollider,treeAim,Vector3.forward,40,200,0);
            Require(treeData.GetFloat(ZDOVars.s_health,1000)==woodHealth, "Wind Scar respects tree tool-tier requirement");
            foreach(string field in new[]{"m_logPrefab","m_stubPrefab"}) {
                var woodPrefab=(GameObject)AccessTools.Field(typeof(TreeBase),field).GetValue(tree);
                var woodObject=UnityEngine.Object.Instantiate(woodPrefab,origin+Vector3.right*45,Quaternion.identity);
                woodFixtures.Add(woodObject);
                var woodData=woodObject.GetComponent<ZNetView>().GetZDO(); woodData.Set(ZDOVars.s_health,1000f);
                yield return new WaitForSeconds(.3f);
                var woodCollider=woodObject.GetComponentsInChildren<Collider>().First();
                WindScar.Impact(player,woodCollider,woodObject.transform.position,Vector3.forward,40,200,3);
                Require(woodData.GetFloat(ZDOVars.s_health,1000)<1000, "Wind Scar cuts wood component " + field);
                ZNetScene.instance.Destroy(woodObject);
            }
            ZNetScene.instance.Destroy(treeObject); woodFixtures.Clear();
            var testWall=GameObject.CreatePrimitive(PrimitiveType.Cube); testWall.layer=LayerMask.NameToLayer("static_solid");
            testWall.transform.position=origin+Vector3.forward*4+Vector3.up; testWall.transform.localScale=new Vector3(8,8,.5f);
            Physics.SyncTransforms(); health=enemy.GetHealth();
            WindScar.Launch(player,player.GetEyePoint(),(enemy.GetCenterPoint()-player.GetEyePoint()).normalized);
            yield return new WaitForSeconds(.6f);
            Require(enemy.GetHealth()==health, "solid wall stops Wind Scar before enemy");
            UnityEngine.Object.Destroy(testWall);
            health=tame.GetHealth();
            WindScar.Impact(player,tame.GetComponentsInChildren<Collider>().First(),tame.GetCenterPoint(),Vector3.forward,40,200,3);
            Require(tame.GetHealth()==health, "Wind Scar does not damage allied creatures");
            WindScar.NextCast=0; player.UseStamina(player.GetStamina()+10);
            Require(!WindScar.TryCast(player), "Wind Scar refuses casting without stamina");
            Require(RuneBag.Accepts(wind), "default bag accepts Wind Scar");
            plugin.InfoLog("BOSSMAGIC_WIND_PASS: hotbar, equipment, cost, cooldown, slash hit, straight trajectory, tree/log/stump woodcut, tool tier, wall, allies, stamina, bag.");
            var bag = RuneBag.Give(player); bag.m_gridPos = new Vector2i(4, 0);
            useHotbar.Invoke(player, new object[] { 5 });
            yield return new WaitForSeconds(.3f);
            Require(RuneBag.OpenItem == bag && InventoryGui.instance.IsContainerOpen(), "bag opens native container UI from hotbar");
            Require(RuneBag.View.GetInventory() == RuneBag.Contents && RuneBag.View.IsOwner(), "native container uses portable inventory");
            var bagGrid = (InventoryGrid)AccessTools.Field(typeof(InventoryGui), "m_containerGrid").GetValue(InventoryGui.instance);
            var playerGrid = (InventoryGrid)AccessTools.Field(typeof(InventoryGui), "m_playerGrid").GetValue(InventoryGui.instance);
            Require(bagGrid.DropItem(player.GetInventory(), rune, 1, new Vector2i(0, 0)), "native drag deposits lightning rune");
            Require(!player.GetInventory().ContainsItem(rune) && RuneBag.Contents.GetAllItems().Count == 1, "deposit removes original and stores one rune");
            Require(!bagGrid.DropItem(player.GetInventory(), sword, 1, new Vector2i(0, 0)), "invalid swap into bag rejected before mutation");
            var storedRune = RuneBag.Contents.GetItemAt(0, 0);
            Require(!playerGrid.DropItem(RuneBag.Contents, storedRune, 1, sword.m_gridPos), "reverse swap cannot smuggle a sword into bag");
            Require(player.GetInventory().ContainsItem(sword) && RuneBag.Contents.GetAllItems().Count == 1, "rejected swaps preserve both inventories");
            Require(!bagGrid.DropItem(player.GetInventory(), bag, 1, new Vector2i(1, 0)), "bag cannot contain itself");
            RuneBag.Contents.MoveItemToThis(player.GetInventory(), surge);
            Require(!player.GetInventory().ContainsItem(surge) && RuneBag.Contents.GetAllItems().Count == 2, "quick transfer deposits Surge");
            // The same Inventory.Save/Load path used by character saves must preserve the bag and nested custom data.
            storedRune.m_customData["BossMagic.TestMarker"] = "preserve-me";
            RuneBag.Close();
            var snapshot = new ZPackage(); player.GetInventory().Save(snapshot);
            var restoredPlayerInventory = new Inventory("PersistenceProbe", null, 8, 4);
            restoredPlayerInventory.Load(new ZPackage(snapshot.GetBase64()));
            var restoredBag = restoredPlayerInventory.GetAllItems().Single(RuneBag.IsBag);
            var restoredContents = RuneBag.Read(restoredBag);
            Require(restoredContents.GetAllItems().Count == 2 && restoredContents.GetAllItems().Any(i => i.m_customData.TryGetValue("BossMagic.TestMarker", out var markerValue) && markerValue == "preserve-me"), "character inventory roundtrip preserves contents and rune metadata");
            var secondBag = RuneBag.Give(player);
            Require(RuneBag.Read(secondBag).GetAllItems().Count == 0, "second bag has independent empty storage");
            Require(RuneBag.Open(player, bag), "bag reopens with saved contents");
            string accepted = RuneBag.AcceptedPrefabs.Value;
            bool oldAutoSave = plugin.Config.SaveOnConfigSet;
            plugin.Config.SaveOnConfigSet = false;
            try {
                RuneBag.AcceptedPrefabs.Value = "Stone";
                storedRune = RuneBag.Contents.GetAllItems().First(i => i.m_dropPrefab.name == MagicSystem.RuneName);
                player.GetInventory().MoveItemToThis(RuneBag.Contents, storedRune);
                Require(player.GetInventory().ContainsItem(storedRune), "removed prefab can still be withdrawn");
                RuneBag.Contents.MoveItemToThis(player.GetInventory(), storedRune);
                Require(player.GetInventory().ContainsItem(storedRune), "removed prefab cannot be deposited again");
                var stone = Add(player, "Stone", 6);
                RuneBag.Contents.MoveItemToThis(player.GetInventory(), stone);
                Require(RuneBag.Contents.GetAllItems().Any(i => i.m_dropPrefab.name == "Stone"), "allowlist changes accept another prefab immediately");
                player.GetInventory().MoveItemToThis(RuneBag.Contents, RuneBag.Contents.GetAllItems().First(i => i.m_dropPrefab.name == "Stone"));
            } finally { RuneBag.AcceptedPrefabs.Value = accepted; plugin.Config.SaveOnConfigSet = oldAutoSave; }
            RuneBag.Contents.MoveItemToThis(player.GetInventory(), storedRune);
            for (int i = RuneBag.Contents.GetAllItems().Count; i < 8; i++) {
                var extraRune = MagicSystem.GiveRune(player); RuneBag.Contents.MoveItemToThis(player.GetInventory(), extraRune);
            }
            var overflow = MagicSystem.GiveRune(player); RuneBag.Contents.MoveItemToThis(player.GetInventory(), overflow);
            Require(RuneBag.Contents.GetAllItems().Count == 8 && player.GetInventory().ContainsItem(overflow), "full bag rejects ninth rune without loss");
            // Drop while open closes the view before the item is cloned into the world.
            var previousDrops = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
            Require(player.DropItem(player.GetInventory(), bag, 1), "filled bag can be dropped");
            Require(RuneBag.OpenItem == null, "moving open bag closes the container safely");
            var droppedBag = UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).First(d => !previousDrops.Contains(d) && RuneBag.IsBag(d.m_itemData) && d.m_itemData.m_customData.ContainsKey(RuneBag.DataKey));
            Require(RuneBag.Read(droppedBag.m_itemData).GetAllItems().Count == 8, "world drop keeps all eight runes");
            AccessTools.Method(typeof(ItemDrop), "Save").Invoke(droppedBag, null);
            droppedBag.m_itemData.m_customData.Clear();
            AccessTools.Method(typeof(ItemDrop), "Load").Invoke(droppedBag, null);
            Require(RuneBag.Read(droppedBag.m_itemData).GetAllItems().Count == 8, "world item serialization preserves bag contents");
            Require(player.Pickup(droppedBag.gameObject, false, false), "filled bag can be picked up again");
            bag = player.GetInventory().GetAllItems().First(i => RuneBag.IsBag(i) && i.m_customData.ContainsKey(RuneBag.DataKey));
            Require(RuneBag.Open(player, bag), "picked up bag reopens");
            yield return new WaitForSeconds(.3f);
            AccessTools.Method(typeof(InventoryGui), "OnTakeAll").Invoke(InventoryGui.instance, null);
            Require(RuneBag.Contents.GetAllItems().Count == 0, "native Take All retrieves bag contents");
            RuneBag.Close();
            var preserved = bag.m_customData[RuneBag.DataKey];
            bag.m_customData[RuneBag.ManifestKey] = "BossMagic_MissingDependency_Test";
            bool missingBlocked = false;
            try { RuneBag.Read(bag); } catch (InvalidOperationException) { missingBlocked = true; }
            Require(missingBlocked && bag.m_customData[RuneBag.DataKey] == preserved, "missing prefab blocks loading without rewriting stored contents");
            bag.m_customData[RuneBag.ManifestKey] = "";
            plugin.InfoLog("BOSSMAGIC_BAG_PASS: native UI, drag, quick transfer, filtering, swaps, nesting, capacity, per-item persistence, world drop/load/pickup, take all, missing dependencies.");
            var chainItem=LightningChain.Give(player);
            Require(chainItem!=null && RuneBag.Accepts(chainItem),"Chain rune registered and accepted by bag");
            var chainTarget=Target(player.transform.position+Vector3.right*6,false);
            var chainExtraA=Target(chainTarget.transform.position+Vector3.forward*1.5f,false);
            var chainExtraB=Target(chainTarget.transform.position-Vector3.forward*1.5f,false);
            yield return new WaitForSeconds(.3f);
            player.AddStamina(200); LightningChain.NextCast=0;
            Aim(player,chainTarget.GetCenterPoint());
            var heldRight=AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player);
            Require(LightningChain.TryCast(player) && LightningChain.Target==chainTarget,"Chain captures aimed creature");
            Require(LightningChain.IsLinked(chainExtraA) && LightningChain.IsLinked(chainExtraB) && LightningChain.TargetCount<=LightningChain.MaxTargets.Value,"Chain captures nearby secondary enemies within limit");
            var extraStartA=chainExtraA.transform.position; var extraStartB=chainExtraB.transform.position;
            var chainStart=chainTarget.transform.position; var chainHealth=chainTarget.GetHealth();
            yield return new WaitForSeconds(.7f);
            Require(Vector3.Distance(chainStart,chainTarget.transform.position)>.5f,"Chain physically drags creature toward leash range");
            Require(Vector3.Distance(extraStartA,chainExtraA.transform.position)>.5f && Vector3.Distance(extraStartB,chainExtraB.transform.position)>.5f,"Chain pulls all secondary targets");
            Require(chainTarget.GetHealth()==chainHealth && AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player)==heldRight,"Chain keeps weapons and does not damage target");
            Camera.main.transform.position=player.GetCenterPoint()+Vector3.back*7+Vector3.up*3;
            Camera.main.transform.rotation=Quaternion.LookRotation((player.GetCenterPoint()+chainTarget.GetCenterPoint())*.5f-Camera.main.transform.position);
            RuneHotbarTest.Capture("lightning-chain.png");
            Require(LightningChain.TryCast(player) && !LightningChain.Target,"Chain reactivation releases target during cooldown");
            Require(LightningChain.TargetCount==0,"Chain cancellation releases every target");
            ZNetScene.instance.Destroy(chainExtraA.gameObject); ZNetScene.instance.Destroy(chainExtraB.gameObject);
            Require(!LightningChain.ValidTarget(tame,player) && !LightningChain.ValidTarget(player,player),"Chain excludes allies and player");
            LightningChain.NextCast=0; Aim(player,chainTarget.GetCenterPoint());
            Require(LightningChain.TryCast(player),"Chain can reacquire released creature");
            var chainWall=GameObject.CreatePrimitive(PrimitiveType.Cube); chainWall.layer=LayerMask.NameToLayer("Default");
            chainWall.transform.position=(chainTarget.GetCenterPoint()+player.GetCenterPoint())*.5f;
            chainWall.transform.localScale=new Vector3(.3f,4,4);
            Physics.SyncTransforms(); LightningChain.Tick();
            Require(!LightningChain.Target,"Chain breaks when solid obstacle blocks tether");
            UnityEngine.Object.Destroy(chainWall); yield return null;
            LightningChain.NextCast=0; Aim(player,chainTarget.GetCenterPoint());
            Require(LightningChain.TryCast(player),"Chain reacquires after obstacle removed");
            chainTarget.transform.position=player.transform.position+Vector3.right*(LightningChain.Range.Value+3);
            Physics.SyncTransforms();
            LightningChain.Tick(); Require(!LightningChain.Target,"Chain breaks beyond maximum range");
            chainTarget.transform.position=player.transform.position+Vector3.right*5; Physics.SyncTransforms();
            LightningChain.NextCast=0; Aim(player,chainTarget.GetCenterPoint());
            player.AddStamina(200);
            var originalDuration=LightningChain.Duration.Value; LightningChain.Duration.Value=1;
            Require(LightningChain.TryCast(player),"Chain starts duration probe");
            yield return new WaitForSeconds(1.2f);
            Require(!LightningChain.Target,"Chain expires and releases creature"); LightningChain.Duration.Value=originalDuration;
            ZNetScene.instance.Destroy(chainTarget.gameObject);
            plugin.InfoLog("BOSSMAGIC_CHAIN_PASS: targeting, physics drag, release, equipment, no damage, ally exclusion, wall and distance break.");
            var cageItem=RootCage.Give(player);
            Require(cageItem!=null && RuneBag.Accepts(cageItem),"Root Cage rune registered and accepted by bag");
            var cageCenter=player.transform.position+Vector3.left*5;
            RootCage.NextCast=0; player.AddStamina(200);
            float beforeCage=player.GetStamina();
            Require(RootCage.TryCast(player) && RootCage.Selecting && player.GetStamina()==beforeCage,"Root Cage enters selection without charging");
            RootCage.Cancel(); Require(!RootCage.Selecting && player.GetStamina()==beforeCage,"Root Cage cancellation is free");
            Require(RootCage.TryCast(player),"Root Cage selection restarts");
            Require(!RootCage.Confirm(player.transform.position+Vector3.up*100) && player.GetStamina()==beforeCage,"Root Cage rejects invalid placement without cost");
            Require(RootCage.GroundArea(player,cageCenter,out var cageGround,out var cagePoints),"Root Cage validates supported circular area");
            Cursor.visible=false; Cursor.lockState=CursorLockMode.Locked;
            Aim(player,cageGround); RootCage.Tick();
            Require(RootCage.Selecting && !(bool)AccessTools.Method(typeof(RootCage),"AttackInput").Invoke(null,new object[]{player}),"Root Cage preview reserves attack input");
            RuneHotbarTest.Capture("root-cage-preview.png");
            Require(RootCage.Confirm(cageGround) && !RootCage.Selecting && RootCage.Roots.Count==RootCage.SegmentCount,"Root Cage creates native root ring on confirmation");
            Require(player.GetStamina()<beforeCage && RootCage.NextCast>Time.time,"Root Cage charges stamina and cooldown only on confirmation");
            yield return new WaitForSeconds(.6f);
            var cageRoot=RootCage.Roots[0];
            Require(cageRoot.GetComponentsInChildren<MeshRenderer>().Any(r=>r.enabled && r.bounds.size.y>1 && r.bounds.size.y<6),"Root Cage animated root remains visible at body height");
            foreach(var root in RootCage.Roots.Take(1)) {
                foreach(var render in root.GetComponentsInChildren<Renderer>()) plugin.InfoLog("CAGE_RENDER_PROBE "+render.GetType().Name+" enabled="+render.enabled+" bounds="+render.bounds+" root="+root.transform.position+" visual="+root.Visual.position+" scale="+root.Visual.localScale);
            }
            for(int i=0;i<RootCage.Roots.Count;i++) {
                var seam=(RootCage.Roots[i].transform.position+RootCage.Roots[(i+1)%RootCage.Roots.Count].transform.position)*.5f-cageGround; seam.y=0;
                Require(Physics.SphereCast(cageGround+Vector3.up*1.5f,.25f,seam.normalized,out var seamHit,RootCage.Radius.Value+1,MagicSystem.WorldMask) && seamHit.collider.GetComponent<CageRoot>(),"Root Cage adjacent root bodies close passage "+i);
            }
            Require(RootCage.Roots.All(r=>r.GetComponent<CapsuleCollider>() && !r.GetComponent<BoxCollider>()),"Root Cage uses individual root bodies, no wall panels");
            var cageEnemy=Target(cageGround+Vector3.up*.2f,false);
            var cageAlly=Target(cageGround+Vector3.up*.2f+Vector3.forward,true);
            var enemyHealthBefore=cageEnemy.GetHealth(); var allyHealthBefore=cageAlly.GetHealth();
            Require(RootCage.Roots.All(r=>!r.CanHit(player) && !r.CanHit(cageAlly)),"Root Cage rejects player and allies as attack targets");
            yield return new WaitForSeconds(3.5f);
            Require(cageEnemy.GetHealth()<enemyHealthBefore && RootCage.Roots.Count(r=>r.Hits>0)>=2,"Root Cage individual roots animate and damage nearby enemy");
            Require(cageAlly.GetHealth()==allyHealthBefore,"Root Cage attacks preserve allied creature health");
            ZNetScene.instance.Destroy(cageEnemy.gameObject); ZNetScene.instance.Destroy(cageAlly.gameObject);
            Require(cageRoot.GetComponentsInChildren<Character>().Length==0 && cageRoot.GetComponentsInChildren<ZNetView>().Length==0 && cageRoot.GetComponentsInChildren<Renderer>().Length>0,"Root Cage native visuals contain no enemy or network behaviour");
            var towardsRoot=cageRoot.transform.position-cageGround; towardsRoot.y=0;
            Require(Physics.Raycast(cageGround+Vector3.up*1.5f,towardsRoot.normalized,out var cageHit,RootCage.Radius.Value+2,MagicSystem.WorldMask) && cageHit.collider.GetComponent<CageRoot>(),"Root Cage collision blocks exit across ring");
            Camera.main.transform.position=cageGround+Vector3.back*12+Vector3.up*9;
            Camera.main.transform.rotation=Quaternion.LookRotation(cageGround+Vector3.up-Camera.main.transform.position);
            RuneHotbarTest.Capture("root-cage.png");
            var collisionProbe=GameObject.CreatePrimitive(PrimitiveType.Sphere); collisionProbe.name="CageCollisionProbe";
            collisionProbe.transform.localScale=Vector3.one*.5f; collisionProbe.transform.position=cageGround+Vector3.up*1.5f+towardsRoot.normalized*(RootCage.Radius.Value-1.2f);
            var probeBody=collisionProbe.AddComponent<Rigidbody>(); probeBody.useGravity=false; probeBody.linearVelocity=towardsRoot.normalized*8;
            yield return new WaitForSeconds(.35f);
            Require(Vector3.Dot(collisionProbe.transform.position-cageGround,towardsRoot.normalized)<RootCage.Radius.Value,"Root Cage blocks moving physics body");
            float neighbourHealth=RootCage.Roots[1].Health;
            var rootDamage=new HitData(); rootDamage.m_damage.m_slash=RootCage.Health.Value+1; cageRoot.Damage(rootDamage);
            Require(RootCage.Roots[1].Health==neighbourHealth,"Root Cage roots have independent health");
            Require(!cageRoot.GetComponent<Collider>().enabled,"Root Cage destroyed segment immediately opens passage");
            probeBody.linearVelocity=towardsRoot.normalized*8;
            yield return new WaitForSeconds(.4f);
            Require(Vector3.Dot(collisionProbe.transform.position-cageGround,towardsRoot.normalized)>RootCage.Radius.Value+.3f,"Root Cage destroyed segment permits physical escape");
            UnityEngine.Object.Destroy(collisionProbe);
            AccessTools.Field(typeof(RootCage),"expires").SetValue(null,Time.time-1); RootCage.Tick();
            Require(RootCage.Roots.Count==0,"Root Cage duration cleanup removes all roots");
            plugin.InfoLog("BOSSMAGIC_CAGE_PASS: selection, cancellation, placement, costs, native visual isolation, collision, destruction, expiration.");
            yield return null;
            VineSwarm.Spread.Value=0;
            Require(VineSwarm.Give(player)!=null,"Vine Swarm rune registered");
            Cursor.visible=false; Cursor.lockState=CursorLockMode.Locked;
            var vineEnemy=Target(player.transform.position+Vector3.forward*7,false);
            yield return new WaitForSeconds(.2f);
            var controls=AccessTools.Method(typeof(Player),"SetControls");
            player.AddStamina(200); VineSwarm.NextCast=0;
            var vineWeapon=AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player);
            Aim(player,vineEnemy.GetCenterPoint());
            Require(VineSwarm.TryCast(player) && VineSwarm.Active,"Vine Swarm begins channel");
            var startVines=VineSwarm.Shots; var startPosition=player.transform.position; var beforeVineHealth=vineEnemy.GetHealth();
            controls.Invoke(player,new object[]{Vector3.forward,false,false,false,false,false,false,true,true,true,true,true});
            Require(((Vector3)AccessTools.Field(typeof(Character),"m_moveDir").GetValue(player)).sqrMagnitude<.001f,"Vine Swarm suppresses movement controls");
            var vineStartTime=Time.time;
            while(Time.time-vineStartTime<VineSwarm.Duration.Value+.3f) { Aim(player,vineEnemy.GetCenterPoint()); yield return new WaitForSeconds(.05f); }
            Require(!VineSwarm.Active && VineSwarm.Shots-startVines>=5,"Vine Swarm emits repeated projectiles and ends naturally");
            Require(Vector3.Distance(player.transform.position,startPosition)<.3f,"Vine Swarm holds player position while channeling");
            Require(vineEnemy.GetHealth()<beforeVineHealth,"Vine Swarm projectiles damage hostile target");
            Require(AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player)==vineWeapon,"Vine Swarm preserves equipped weapon");
            controls.Invoke(player,new object[]{Vector3.forward,false,false,false,false,false,false,false,false,false,false,false});
            Require(((Vector3)AccessTools.Field(typeof(Character),"m_moveDir").GetValue(player)).sqrMagnitude>.1f,"Vine Swarm restores movement input after channel");
            controls.Invoke(player,new object[]{Vector3.zero,false,false,false,false,false,false,false,false,false,false,false});
            player.AddStamina(200); VineSwarm.NextCast=0; Aim(player,vineEnemy.GetCenterPoint());
            Require(VineSwarm.TryCast(player),"Vine Swarm restarts for cancellation test");
            float cancelStamina=player.GetStamina(); int cancelShots=VineSwarm.Shots;
            Require(VineSwarm.TryCast(player) && !VineSwarm.Active && player.GetStamina()==cancelStamina,"Vine Swarm reuse cancels without extra cost");
            yield return new WaitForSeconds(.3f); Require(VineSwarm.Shots==cancelShots,"Vine Swarm cancellation stops further emission");
            player.AddStamina(200); VineSwarm.NextCast=0; Require(VineSwarm.TryCast(player),"Vine Swarm restarts for block cancel");
            controls.Invoke(player,new object[]{Vector3.zero,false,false,false,false,true,true,false,false,false,false,false});
            Require(!VineSwarm.Active,"Vine Swarm blocking cancels and frees player");
            controls.Invoke(player,new object[]{Vector3.zero,false,false,false,false,false,false,false,false,false,false,false});
            var vineProbe=VineSwarm.Launch(player,player.GetCenterPoint()+Vector3.up,Vector3.right);
            yield return new WaitForSeconds(.08f);
            Camera.main.transform.position=player.GetCenterPoint()+Vector3.back*5+Vector3.up*2;
            Camera.main.transform.rotation=Quaternion.LookRotation(player.GetCenterPoint()+Vector3.right*3-Camera.main.transform.position);
            RuneHotbarTest.Capture("vine-swarm.png");
            Require(vineProbe && vineProbe.GetComponentsInChildren<Projectile>().Length==0 && vineProbe.GetComponentsInChildren<ZNetView>().Length==0,"Vine Swarm isolates native projectile visuals from gameplay scripts");
            UnityEngine.Object.Destroy(vineProbe.gameObject); ZNetScene.instance.Destroy(vineEnemy.gameObject);
            plugin.InfoLog("BOSSMAGIC_VINES_PASS: channel, movement lock/restore, repeated projectiles, damage, equipment, reuse cancel, block cancel, native visuals.");
            var hotbarTest = RuneHotbarTest.Run(player);
            while (hotbarTest.MoveNext()) yield return hotbarTest.Current;
            var naturalObject = UnityEngine.Object.Instantiate(PrefabManager.Instance.GetPrefab("Greydwarf"), player.transform.position + Vector3.right * 3, Quaternion.identity);
            var natural = naturalObject.GetComponent<Character>();
            Require(!TestArea.IsSubject(natural), "ordinary creature is not a test subject");
            Require(TestArea.IsSubject(enemy) && TestArea.IsSubject(tame), "test subjects carry persistent identification");
            int cleaned = TestArea.Reset(player.transform.position, 300);
            Require(cleaned >= 2 && natural, "reset removes test enemies and allies while preserving ordinary creature");
            ZNetScene.instance.Destroy(naturalObject);
            yield return null;
            Require(TestArea.Reset(player.transform.position, 300) == 0, "reset is safe to repeat");
            plugin.InfoLog("BOSSMAGIC_AREA_PASS: marked targets, selective cleanup, repeated reset, automatic test cleanup.");
            player.SetGodMode(false);
            plugin.InfoLog("BOSSMAGIC_GAMEPLAY_PASS: hotbar, equipment, reuse, stamina, cooldown, projectile, electric damage, knockback, tame exclusion, wall occlusion.");
            RestoreTestConfig();
            Application.Quit(0);
        }
    }
}






























