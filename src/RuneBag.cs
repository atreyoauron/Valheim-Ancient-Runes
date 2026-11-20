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
    // A local, inactive Container provides vanilla UI only. Item custom data owns the contents.
    internal static class RuneBag
    {
        internal const string PrefabName = "BossMagic_RuneBag";
        internal const string DataKey = "BossMagic.RuneBag.v1";
        internal const string ManifestKey = "BossMagic.RuneBag.prefabs.v1";
        internal static ConfigEntry<string> AcceptedPrefabs;
        internal static Inventory Contents;
        internal static ItemDrop.ItemData OpenItem;
        internal static Container View;
        internal static bool Ready;
        private static Player owner;
        private static bool closing;
        private static readonly System.Reflection.FieldInfo Current = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        internal static bool IsBag(ItemDrop.ItemData item) => item?.m_dropPrefab && item.m_dropPrefab.name == PrefabName;
        internal static bool Accepts(ItemDrop.ItemData item) => item?.m_dropPrefab && !IsBag(item) &&
            AcceptedPrefabs.Value.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(name => string.Equals(name.Trim(), item.m_dropPrefab.name, StringComparison.Ordinal));

        internal static void Initialize(Plugin host, Harmony harmony)
        {
            AcceptedPrefabs = host.Config.Bind("RuneBag", "AcceptedPrefabs", MagicSystem.RuneName + "," + SurgeSystem.RuneName + "," + BeaconSystem.RuneName + "," + WindScar.RuneName + "," + LightningChain.RuneName + "," + RootCage.RuneName + "," + VineSwarm.RuneName + "," + NatureBlessing.RuneName,
                new ConfigDescription("Nomes exatos dos prefabs aceitos, separados por virgula. Remover um nome impede novos depositos, mas permite retirar itens guardados. Bolsas de runas deste mod nunca podem ser guardadas em outra bolsa.",null,new ConfigurationManagerAttributes { IsAdminOnly=true }));
            if (AcceptedPrefabs.Value == MagicSystem.RuneName + "," + SurgeSystem.RuneName)
                AcceptedPrefabs.Value += "," + BeaconSystem.RuneName;
            if (AcceptedPrefabs.Value == MagicSystem.RuneName + "," + SurgeSystem.RuneName + "," + BeaconSystem.RuneName)
                AcceptedPrefabs.Value += "," + WindScar.RuneName;
            if (AcceptedPrefabs.Value == MagicSystem.RuneName + "," + SurgeSystem.RuneName + "," + BeaconSystem.RuneName + "," + WindScar.RuneName)
                AcceptedPrefabs.Value += "," + LightningChain.RuneName;
            if (AcceptedPrefabs.Value == MagicSystem.RuneName + "," + SurgeSystem.RuneName + "," + BeaconSystem.RuneName + "," + WindScar.RuneName + "," + LightningChain.RuneName)
                AcceptedPrefabs.Value += "," + RootCage.RuneName;
            if (AcceptedPrefabs.Value == MagicSystem.RuneName + "," + SurgeSystem.RuneName + "," + BeaconSystem.RuneName + "," + WindScar.RuneName + "," + LightningChain.RuneName + "," + RootCage.RuneName)
                AcceptedPrefabs.Value += "," + VineSwarm.RuneName;
            if (AcceptedPrefabs.Value == MagicSystem.RuneName + "," + SurgeSystem.RuneName + "," + BeaconSystem.RuneName + "," + WindScar.RuneName + "," + LightningChain.RuneName + "," + RootCage.RuneName + "," + VineSwarm.RuneName) AcceptedPrefabs.Value += "," + NatureBlessing.RuneName;
            PrefabManager.OnVanillaPrefabsAvailable += Register;
            CommandManager.Instance.AddConsoleCommand(new BagCommand());
            Patch(harmony, typeof(Humanoid), "UseItem", nameof(Use));
            Patch(harmony, typeof(Container), "IsOwner", nameof(IsOwner));
            Patch(harmony, typeof(Container), "SetInUse", nameof(SetInUse));
            Patch(harmony, typeof(Container), "StackAll", nameof(StackContainer));
            Patch(harmony, typeof(Container), "TakeAll", nameof(TakeAll));
            Patch(harmony, typeof(InventoryGrid), "DropItem", nameof(Drop));
            Patch(harmony, typeof(Inventory), "StackAll", nameof(Stack));
            foreach (var method in typeof(Inventory).GetMethods().Where(m => m.Name == "AddItem" && m.ReturnType == typeof(bool) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RuneBag), nameof(Add)));
            foreach (var method in typeof(Inventory).GetMethods().Where(m => m.Name == "RemoveItem" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(ItemDrop.ItemData)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RuneBag), nameof(Removing)));
        }
        private static void Patch(Harmony h, Type t, string method, string prefix) => h.Patch(AccessTools.Method(t, method), prefix: new HarmonyMethod(typeof(RuneBag), prefix));
        private static void Register()
        {
            var bag = new CustomItem(PrefabName, "LeatherScraps", new ItemConfig { Name = "Bolsa de Runas", Description = "8 espacos para runas. Use com o botao direito ou pela hotbar para abrir. Arraste runas para a hotbar azul para criar atalhos. Prefabs aceitos configuraveis no F1." });
            var shared = bag.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material;
            shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = 1f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var colors = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                bool body = Mathf.Pow((x - 32f) / 23, 2) + Mathf.Pow((y - 27f) / 24, 2) < 1;
                bool neck = y >= 45 && y < 57 && x > 20 && x < 44;
                bool bolt = y > 14 && y < 41 && Math.Abs(x - (26 + (y % 14))) < 3;
                colors[y * 64 + x] = body || neck ? (bolt ? Color.cyan : y > 45 && y < 50 ? new Color(.12f, .08f, .04f) : new Color(.45f, .28f, .12f)) : Color.clear;
            }
            texture.SetPixels(colors); texture.Apply();
            shared.m_icons = new[] { Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f)) };
            if (!ItemManager.Instance.AddItem(bag)) throw new InvalidOperationException("Rune bag registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            Plugin.instance.InfoLog("BOSSMAGIC_BAG_REGISTERED");
        }
        private static void Say(string text) { if (Player.m_localPlayer) Player.m_localPlayer.Message(MessageHud.MessageType.Center, text); }
        private static bool Use(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (!IsBag(item)) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory())) Open(player, item);
            return false;
        }
        internal static Inventory Read(ItemDrop.ItemData item)
        {
            var inventory = new Inventory("Bolsa de Runas", null, 4, 2);
            if (item.m_customData.TryGetValue(DataKey, out var saved)) {
                if (!item.m_customData.TryGetValue(ManifestKey, out var manifest)) throw new InvalidOperationException("Missing bag manifest; contents preserved");
                foreach (string prefab in manifest.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!ObjectDB.instance.GetItemPrefab(prefab)) throw new InvalidOperationException("Missing item prefab: " + prefab);
                inventory.Load(new ZPackage(saved));
            }
            return inventory;
        }
        internal static bool Open(Player player, ItemDrop.ItemData item)
        {
            if (!ZNet.instance || !IsBag(item) || !player.GetInventory().ContainsItem(item) || player.IsDead() || player.IsTeleporting()) return false;
            if (OpenItem == item) { Close(); return true; }
            Close();
            Inventory loaded;
            try { loaded = Read(item); }
            catch (Exception e) { Plugin.instance.ErrorLog("BOSSMAGIC_BAG_LOAD_BLOCKED " + e); Say("Nao foi possivel carregar a bolsa. Conteudo preservado; confira os mods dos itens guardados."); return false; }
            if (InventoryGui.instance.IsContainerOpen()) InventoryGui.instance.Hide();
            owner = player; OpenItem = item; Contents = loaded;
            var obj = new GameObject("BossMagic_BagView"); obj.SetActive(false);
            obj.transform.SetParent(player.transform, false);
            View = obj.AddComponent<Container>();
            AccessTools.Field(typeof(Container), "m_inventory").SetValue(View, Contents);
            Contents.m_onChanged += Save;
            InventoryGui.instance.Show(View, 1);
            AccessTools.Field(typeof(InventoryGui), "m_firstContainerUpdate").SetValue(InventoryGui.instance, true);
            Plugin.instance.InfoLog("BOSSMAGIC_BAG_OPEN items=" + Contents.GetAllItems().Count);
            return true;
        }
        internal static void Save()
        {
            if (OpenItem == null || Contents == null) return;
            var package = new ZPackage(); Contents.Save(package);
            OpenItem.m_customData[DataKey] = package.GetBase64();
            OpenItem.m_customData[ManifestKey] = string.Join("\n", Contents.GetAllItems().Select(i => i.m_dropPrefab.name).Distinct());
        }
        internal static void Close()
        {
            if (closing || OpenItem == null) return;
            closing = true;
            try {
                Save();
                if (InventoryGui.instance && Current.GetValue(InventoryGui.instance) as Container == View) InventoryGui.instance.Hide();
                Contents.m_onChanged -= Save;
                if (View) UnityEngine.Object.Destroy(View.gameObject);
                Contents = null; OpenItem = null; View = null; owner = null;
            } finally { closing = false; }
        }
        internal static void Tick()
        {
            if (OpenItem == null) return;
            if (!owner || owner != Player.m_localPlayer || owner.IsDead() || owner.IsTeleporting() || !owner.GetInventory().ContainsItem(OpenItem) ||
                !InventoryGui.instance || Current.GetValue(InventoryGui.instance) as Container != View || !ZNet.instance) Close();
        }
        private static bool IsOwner(Container __instance, ref bool __result) { if (__instance != View || !View) return true; __result = true; return false; }
        private static bool SetInUse(Container __instance) => !View || __instance != View;
        private static bool StackContainer(Container __instance) { if (!View || __instance != View) return true; Contents.StackAll(owner.GetInventory(), false); return false; }
        private static bool TakeAll(Container __instance, Humanoid __0, ref bool __result) { if (!View || __instance != View) return true; __0.GetInventory().MoveAll(Contents); __result = true; return false; }
        private static bool Add(Inventory __instance, ItemDrop.ItemData __0, ref bool __result)
        {
            if (Contents == null || __instance != Contents || Accepts(__0) || Contents.ContainsItem(__0)) return true;
            Say("Esta bolsa nao aceita esse item. Configure os prefabs no F1."); __result = false; return false;
        }
        private static bool Drop(InventoryGrid __instance, Inventory __0, ItemDrop.ItemData __1, int __2, Vector2i __3, ref bool __result)
        {
            if (Contents == null) return true;
            var destination = __instance.GetInventory();
            var other = destination.GetItemAt(__3.x, __3.y);
            // Validate both sides before vanilla removes either item during a swap.
            if ((destination == Contents && __0 != Contents && !Accepts(__1)) ||
                (__0 == Contents && destination != Contents && other != null && !Accepts(other))) {
                Say("Troca bloqueada: a bolsa aceita apenas os prefabs configurados."); __result = false; return false;
            }
            // Existing items remain movable inside the bag even after their prefab is removed from the allowlist.
            if (__0 == Contents && destination == Contents && !Accepts(__1)) {
                var old = __1.m_gridPos; __1.m_gridPos = __3; if (other != null && other != __1) other.m_gridPos = old;
                Save(); __result = true; return false;
            }
            return true;
        }
        private static bool Stack(Inventory __instance, Inventory __0, ref int __result)
        {
            if (Contents == null || __instance != Contents) return true;
            int count = 0;
            foreach (var item in __0.GetAllItems().ToArray()) {
                if (!Accepts(item) || !Contents.GetAllItems().Any(i => i.m_shared.m_name == item.m_shared.m_name)) continue;
                int before = item.m_stack; Contents.MoveItemToThis(__0, item);
                count += __0.ContainsItem(item) ? before - item.m_stack : before;
            }
            __result = count; return false;
        }
        private static void Removing(Inventory __instance, ItemDrop.ItemData __0)
        {
            if (OpenItem != null && __0 == OpenItem && owner && __instance == owner.GetInventory()) Close();
        }
        internal static ItemDrop.ItemData Give(Player player)
        {
            var prefab = PrefabManager.Instance.GetPrefab(PrefabName);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab;
            if (!player.GetInventory().AddItem(item)) { Say("Inventario cheio."); return null; }
            return item;
        }
        private sealed class BagCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_bag";
            public override string Help => "Entrega uma bolsa de runas com 8 espacos experimental.";
            public override void Run(string[] args) { if (Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }
}






