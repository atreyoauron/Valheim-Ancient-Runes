using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BossMagic.Test
{
    internal static class RuneHotbar
    {
        internal const string IdentityKey = "BossMagic.RuneIdentity.v1";
        internal const string BindingPrefix = "BossMagic.RuneHotbar.v1.";
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static bool RuneMode;
        internal static Player CurrentPlayer;
        internal static GameObject CanvasObject;
        internal static RectTransform Panel;
        private static RectTransform vanillaRect;
        private static HotkeyBar vanillaBar;
        private static Vector2 originalPosition;
        private static readonly Image[] slotBackgrounds = new Image[8], icons = new Image[8];
        private static readonly Text[] numbers = new Text[8], levels = new Text[8];
        private static readonly ItemDrop.ItemData[] available = new ItemDrop.ItemData[8];
        private static readonly string[] bindings = new string[8], prefabs = new string[8];
        private static Text heading, equipmentHeading, hint;
        private static Image runeIndicator, equipmentIndicator;
        internal static ConfigEntry<Color> RuneArrowColor, RuneOutlineColor, EquipmentArrowColor, EquipmentOutlineColor;
        private static RectTransform runeFrame, equipmentFrame, shiftedContainer;
        private static Vector2 containerPosition;
        private static Font font;
        private static float nextRefresh;
        private static int hover = -1;
        private static readonly System.Reflection.MethodInfo TakeInput = AccessTools.Method(typeof(Player), "TakeInput");
        private static readonly System.Reflection.FieldInfo DragItem = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly System.Reflection.FieldInfo DragInventory = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly System.Reflection.MethodInfo SetupDrag = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
        private static int lastToggleFrame = -1;

        internal static void Initialize(Plugin host, Harmony harmony)
        {
            Enabled = host.Config.Bind("RuneHotbar", "Enabled", true, "Exibe a hotbar azul de oito atalhos para runas individuais nas bolsas carregadas.");
            ToggleKey = host.Config.Bind("RuneHotbar", "ToggleKey", KeyCode.CapsLock, "Alterna entre equipamentos e runas. Independente do estado de maiusculas do Windows. Bloqueada ao digitar ou em menus.");
            RuneArrowColor=host.Config.Bind("RuneHotbar", "RuneArrowColor", new Color(.2f,.7f,1f,1f), "Cor da seta das runas. Aplicada imediatamente.");
            RuneOutlineColor=host.Config.Bind("RuneHotbar", "RuneOutlineColor", new Color(.2f,.7f,1f,.85f), "Cor do contorno ativo das runas. Aplicada imediatamente.");
            EquipmentArrowColor=host.Config.Bind("RuneHotbar", "EquipmentArrowColor", new Color(1f,.76f,.22f,1f), "Cor da seta dos equipamentos. Aplicada imediatamente.");
            EquipmentOutlineColor=host.Config.Bind("RuneHotbar", "EquipmentOutlineColor", new Color(1f,.76f,.22f,.85f), "Cor do contorno ativo dos equipamentos. Aplicada imediatamente.");
            harmony.Patch(AccessTools.Method(typeof(Player), "UseHotbarItem"), prefix: new HarmonyMethod(typeof(RuneHotbar), nameof(RouteHotbar)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Update"), prefix: new HarmonyMethod(typeof(RuneHotbar), nameof(PlayerUpdate)));
            harmony.Patch(AccessTools.Method(typeof(InventoryGui), "OnDropOutside"), prefix: new HarmonyMethod(typeof(RuneHotbar), nameof(DropOutside)));
            harmony.Patch(AccessTools.Method(typeof(Inventory), "Changed"), postfix: new HarmonyMethod(typeof(RuneHotbar), nameof(InventoryChanged)));
        }
        internal static bool Supported(ItemDrop.ItemData item) => item?.m_dropPrefab &&
            new[] { MagicSystem.RuneName, SurgeSystem.RuneName, BeaconSystem.RuneName, WindScar.RuneName, LightningChain.RuneName, RootCage.RuneName, VineSwarm.RuneName, NatureBlessing.RuneName }.Contains(item.m_dropPrefab.name);
        internal static void EnsurePlayer(Player player)
        {
            if (CurrentPlayer == player) return;
            CurrentPlayer = player; RuneMode = false;
            ReloadBindings(); nextRefresh = 0;
        }
        internal static void ReloadBindings()
        {
            for (int i=0;i<8;i++) {
                bindings[i] = prefabs[i] = null; available[i] = null;
                if (CurrentPlayer && CurrentPlayer.m_customData.TryGetValue(BindingPrefix+i, out var saved)) {
                    var parts=saved.Split('|');
                    if(parts.Length==2 && Guid.TryParseExact(parts[0],"N",out var id)) { bindings[i]=parts[0]; prefabs[i]=parts[1]; }
                }
            }
        }
        internal static string Identity(ItemDrop.ItemData item)
        {
            if (!item.m_customData.TryGetValue(IdentityKey,out var id) || !Guid.TryParseExact(id,"N",out var parsed)) {
                id=Guid.NewGuid().ToString("N"); item.m_customData[IdentityKey]=id;
            }
            return id;
        }
        internal static bool Bind(int slot, ItemDrop.ItemData item, Inventory source)
        {
            var player=Player.m_localPlayer; EnsurePlayer(player);
            if(slot<0 || slot>=8 || !player || !Enabled.Value || !Supported(item) || item.m_stack!=1 || source!=RuneBag.Contents ||
                RuneBag.OpenItem==null || !player.GetInventory().ContainsItem(RuneBag.OpenItem) || !source.ContainsItem(item)) return false;
            bindings[slot]=Identity(item); prefabs[slot]=item.m_dropPrefab.name;
            player.m_customData[BindingPrefix+slot]=bindings[slot]+"|"+prefabs[slot];
            RuneBag.Save(); nextRefresh=0;
            return true;
        }
        internal static void Clear(int slot)
        {
            if(!CurrentPlayer || slot<0 || slot>=8) return;
            CurrentPlayer.m_customData.Remove(BindingPrefix+slot); bindings[slot]=prefabs[slot]=null; available[slot]=null;
        }
        // Always traverse only bags actually in the local player's inventory. No cached permission to cast.
        internal static ItemDrop.ItemData Resolve(Player player, int slot)
        {
            EnsurePlayer(player);
            if(!player || slot<0 || slot>=8 || bindings[slot]==null) return null;
            return Match(Collect(player),slot);
        }
        private static List<ItemDrop.ItemData> Collect(Player player)
        {
            var result=new List<ItemDrop.ItemData>();
            if(!player) return result;
            foreach(var bag in player.GetInventory().GetAllItems().Where(RuneBag.IsBag).ToArray()) {
                Inventory inventory;
                try { inventory=bag==RuneBag.OpenItem && RuneBag.Contents!=null ? RuneBag.Contents : RuneBag.Read(bag); }
                catch { continue; } // Missing/corrupt bag content is unavailable; never rewrite it here.
                result.AddRange(inventory.GetAllItems());
            }
            return result;
        }
        private static ItemDrop.ItemData Match(List<ItemDrop.ItemData> items,int slot)
        {
            ItemDrop.ItemData found=null;
            if(bindings[slot]==null) return null;
            foreach(var item in items) {
                if(!Supported(item) || item.m_stack!=1 || !item.m_customData.TryGetValue(IdentityKey,out var id) || id!=bindings[slot] || item.m_dropPrefab.name!=prefabs[slot]) continue;
                if(found!=null) return null; // Ambiguous duplicate identity: fail closed, never choose another exemplar.
                found=item;
            }
            return found;
        }
        private static bool InWorld => ZNet.instance;
        internal static bool CanUseInput(Player player) => player && Hud.instance && InWorld && (bool)TakeInput.Invoke(player,null) &&
            !Hud.InRadial() && !Hud.IsPieceSelectionVisible() && !Cursor.visible;
        internal static bool Toggle(Player player)
        {
            EnsurePlayer(player);
            if(!Enabled.Value || !CanUseInput(player)) return false;
            RuneMode=!RuneMode; nextRefresh=0; return true;
        }
        private static void PlayerUpdate(Player __instance)
        {
            if(__instance!=Player.m_localPlayer) return;
            EnsurePlayer(__instance);
            if(Input.GetKeyDown(ToggleKey.Value) && lastToggleFrame!=Time.frameCount) { lastToggleFrame=Time.frameCount; Toggle(__instance); }
        }
        private static bool RouteHotbar(Player __instance,int __0)
        {
            if(__instance!=Player.m_localPlayer) return true;
            EnsurePlayer(__instance);
            if(!Enabled.Value || !RuneMode) return true;
            if(CanUseInput(__instance)) Cast(__instance,__0-1);
            return false; // Never fall through to an equipment slot, even when a rune is missing.
        }
        internal static bool Cast(Player player,int slot)
        {
            if(!Enabled.Value || !InWorld || !player || player!=Player.m_localPlayer || player.IsDead() || player.InCutscene() || player.IsTeleporting()) return false;
            var item=Resolve(player,slot);
            if(item==null) { MagicSystem.Say(player,"Runa indisponivel: mantenha este exemplar em uma bolsa no inventario."); return false; }
            if(VineSwarm.Active && item.m_dropPrefab.name!=VineSwarm.RuneName) VineSwarm.Stop();
            switch(item.m_dropPrefab.name) {
                case MagicSystem.RuneName: return MagicSystem.TryCast(player);
                case SurgeSystem.RuneName: return SurgeSystem.TryCast(player);
                case BeaconSystem.RuneName: return BeaconSystem.TryCast(player);
                case NatureBlessing.RuneName: return NatureBlessing.TryCast(player);
                case VineSwarm.RuneName: return VineSwarm.TryCast(player);
                case RootCage.RuneName: return RootCage.TryCast(player);
                case LightningChain.RuneName: return LightningChain.TryCast(player);
                case WindScar.RuneName: return WindScar.TryCast(player);
            }
            return false;
        }
        private static void InventoryChanged() { nextRefresh=0; }
        private static bool DropOutside() => !OverPanel();
        private static bool OverPanel() => Enabled.Value && Panel && Panel.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(Panel,Input.mousePosition,null);
        internal static bool BindDragged(int slot)
        {
            if(!InventoryGui.IsVisible() || !InventoryGui.instance) return false;
            var item=DragItem.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            var source=DragInventory.GetValue(InventoryGui.instance) as Inventory;
            bool bound=Bind(slot,item,source);
            if(bound) { SetupDrag.Invoke(InventoryGui.instance,new object[]{null,null,1}); Refresh(); }
            else if(item!=null) MagicSystem.Say(Player.m_localPlayer,"Arraste uma das oito runas do mod de uma bolsa carregada.");
            return bound;
        }
        internal static void Click(int slot, bool right)
        {
            if(!InventoryGui.IsVisible()) return;
            if(right) { Clear(slot); Refresh(); } else BindDragged(slot);
        }
        internal static void Hover(int slot) { hover=slot; }
        internal static void Tick()
        {
            var player=Player.m_localPlayer; EnsurePlayer(player);
            if(!Enabled.Value || !player || !Hud.instance) { RuneMode=false; DestroyUI(); return; }
            if(!CanvasObject) CreateUI();
            if(!CanvasObject) return;
            bool visible=!player.IsDead() && !player.InCutscene() && !player.IsTeleporting() && Hud.instance.m_rootObject.activeInHierarchy;
            CanvasObject.SetActive(visible);
            if(!visible) return;
            Layout();
            if(Time.unscaledTime>=nextRefresh) { Refresh(); nextRefresh=Time.unscaledTime+.25f; }
            heading.text=(RuneMode?"RUNAS ATIVAS":"RUNAS")+"  ·  "+ToggleKey.Value;
            equipmentHeading.text=RuneMode?"EQUIPAMENTOS":"EQUIPAMENTOS ATIVOS";
            runeIndicator.color=RuneArrowColor.Value; runeIndicator.gameObject.SetActive(RuneMode);
            equipmentIndicator.color=EquipmentArrowColor.Value; equipmentIndicator.gameObject.SetActive(!RuneMode && !InventoryGui.IsVisible());
            foreach(var edge in runeFrame.GetComponentsInChildren<Image>(true)) edge.color=RuneOutlineColor.Value;
            foreach(var edge in equipmentFrame.GetComponentsInChildren<Image>(true)) edge.color=EquipmentOutlineColor.Value;
            runeFrame.gameObject.SetActive(RuneMode);
            equipmentFrame.gameObject.SetActive(!RuneMode && !InventoryGui.IsVisible());
            hint.text=InventoryGui.IsVisible()?"Bolsa → arraste para vincular  |  Botao direito: remover atalho":"";
            if(hover>=0 && hover<8 && prefabs[hover]!=null && InventoryGui.IsVisible()) {
                var prefab=PrefabManager.Instance.GetPrefab(prefabs[hover]);
                var name=prefab?Localization.instance.Localize(prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name):prefabs[hover];
                hint.text=name+(available[hover]!=null?" — exemplar disponivel":" — exemplar ausente ou duplicado");
            }
        }
        internal static void Refresh()
        {
            var items=Collect(CurrentPlayer);
            for(int i=0;i<8;i++) {
                available[i]=Match(items,i);
                if(!icons[i]) continue;
                var item=available[i]; var prefab=prefabs[i]!=null?PrefabManager.Instance.GetPrefab(prefabs[i]):null;
                icons[i].sprite=item!=null?item.GetIcon():prefab?prefab.GetComponent<ItemDrop>().m_itemData.GetIcon():null;
                icons[i].enabled=icons[i].sprite; icons[i].color=item!=null?Color.white:new Color(.45f,.45f,.45f,.55f);
                levels[i].text=item!=null?"N"+item.m_quality:bindings[i]!=null?"Ausente":"";
                slotBackgrounds[i].color=item!=null?new Color(.06f,.25f,.48f,.95f):new Color(.035f,.09f,.16f,.9f);
            }
        }
        private static RectTransform Rect(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var obj=new GameObject(name,typeof(RectTransform)); var rect=obj.GetComponent<RectTransform>(); rect.SetParent(parent,false);
            rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1); rect.anchoredPosition=position; rect.sizeDelta=size; return rect;
        }
        private static Text Label(string name,Transform parent,string text,int size)
        {
            var rect=Rect(name,parent,Vector2.zero,Vector2.one); var label=rect.gameObject.AddComponent<Text>();
            label.font=font; label.fontSize=size; label.text=text; label.color=Color.white; label.raycastTarget=false;
            label.horizontalOverflow=HorizontalWrapMode.Overflow; label.verticalOverflow=VerticalWrapMode.Overflow;
            return label;
        }
        private static void CreateUI()
        {
            vanillaBar=Hud.instance.GetComponentInChildren<HotkeyBar>(true);
            if(!vanillaBar) return;
            vanillaRect=vanillaBar.GetComponent<RectTransform>(); originalPosition=vanillaRect.anchoredPosition;
            vanillaRect.anchoredPosition=originalPosition+Vector2.down*68;
            font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            CanvasObject=new GameObject("BossMagic_RuneHotbar",typeof(RectTransform),typeof(Canvas),typeof(GraphicRaycaster));
            var canvas=CanvasObject.GetComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=30000;
            Panel=Rect("RuneBar",CanvasObject.transform,Vector2.zero,new Vector2(600,90));
            runeFrame=Frame("RuneOutline",Panel,RuneOutlineColor.Value);
            equipmentFrame=Frame("EquipmentOutline",Panel,EquipmentOutlineColor.Value);
            heading=Label("Runes",Panel,"RUNAS",14); equipmentHeading=Label("Equipment",Panel,"EQUIPAMENTOS",12);
            runeIndicator=Rect("RuneActive",Panel,Vector2.zero,new Vector2(9,9)).gameObject.AddComponent<Image>(); runeIndicator.raycastTarget=false;
            equipmentIndicator=Rect("EquipmentActive",Panel,Vector2.zero,new Vector2(9,9)).gameObject.AddComponent<Image>(); equipmentIndicator.raycastTarget=false;
            var arrowTexture=new Texture2D(32,32,TextureFormat.RGBA32,false); var pixels=new Color[1024];
            for(int y=0;y<32;y++) for(int x=0;x<32;x++) {
                bool shaft=x>=2 && x<17 && y>=11 && y<=20;
                bool tip=x>=13 && x<=30 && Mathf.Abs(y-15.5f)<=(30-x)*.85f;
                pixels[y*32+x]=shaft||tip?Color.white:Color.clear;
            }
            arrowTexture.SetPixels(pixels); arrowTexture.Apply();
            var arrow=Sprite.Create(arrowTexture,new Rect(0,0,32,32),new Vector2(.5f,.5f));
            runeIndicator.sprite=equipmentIndicator.sprite=arrow;
            foreach(var indicator in new[]{runeIndicator,equipmentIndicator}) {
                var shadow=indicator.gameObject.AddComponent<Shadow>(); shadow.effectColor=new Color(0,0,0,.75f); shadow.effectDistance=new Vector2(1,-1);
            }
            hint=Label("Hint",Panel,"",12);
            for(int i=0;i<8;i++) {
                var rect=Rect("RuneSlot"+(i+1),Panel,Vector2.zero,new Vector2(64,64)); slotBackgrounds[i]=rect.gameObject.AddComponent<Image>();
                var outline=rect.gameObject.AddComponent<Outline>(); outline.effectColor=new Color(.15f,.6f,1,.9f); outline.effectDistance=new Vector2(1,-1);
                var events=rect.gameObject.AddComponent<RuneHotbarSlot>(); events.Slot=i;
                icons[i]=Rect("Icon",rect,new Vector2(6,-6),new Vector2(52,52)).gameObject.AddComponent<Image>(); icons[i].preserveAspect=true; icons[i].raycastTarget=false;
                numbers[i]=Label("Key",rect,(i+1).ToString(),13); levels[i]=Label("Level",rect,"",11);
            }
            Layout(); Refresh();
            Plugin.instance.InfoLog("BOSSMAGIC_HOTBAR_UI_CREATED native="+vanillaRect.name+" position="+originalPosition+" spacing="+vanillaBar.m_elementSpace);
        }
        private static RectTransform Frame(string name,Transform parent,Color color)
        {
            var frame=Rect(name,parent,Vector2.zero,Vector2.one);
            for(int i=0;i<4;i++) {
                var edge=Rect("Edge"+i,frame,Vector2.zero,Vector2.zero);
                var img=edge.gameObject.AddComponent<Image>(); img.color=color; img.raycastTarget=false;
                edge.anchorMin=i==0?new Vector2(0,1):Vector2.zero;
                edge.anchorMax=i==1?new Vector2(1,0):i==0?Vector2.one:i==2?new Vector2(0,1):Vector2.one;
                if(i==3) edge.anchorMin=new Vector2(1,0);
                edge.pivot=new Vector2(.5f,.5f); edge.sizeDelta=i<2?new Vector2(0,2):new Vector2(2,0);
            }
            var tab=Rect("ActiveTab",frame,Vector2.zero,Vector2.zero);
            tab.anchorMin=new Vector2(1,0); tab.anchorMax=new Vector2(1,1); tab.pivot=new Vector2(0,.5f);
            var fill=tab.gameObject.AddComponent<Image>(); fill.color=color; fill.raycastTarget=false;
            return frame;
        }
        private static void RestoreContainer()
        {
            if(shiftedContainer) shiftedContainer.anchoredPosition=containerPosition;
            shiftedContainer=null;
        }
        private static void Layout()
        {
            if(!vanillaRect || !Panel) return;
            float scale=Mathf.Max(.5f,vanillaRect.lossyScale.x);
            float spacing=vanillaBar.m_elementSpace*scale, size=Mathf.Max(32,spacing-4*scale);
            Vector3 origin=RectTransformUtility.WorldToScreenPoint(null,vanillaRect.position);
            // Pixel-space overlay follows the game's scaled hotbar. Only its visual position moves.
            Panel.anchorMin=Panel.anchorMax=Vector2.zero;
            bool editing=InventoryGui.IsVisible();
            Panel.position=new Vector3(Mathf.Max(8,origin.x),Mathf.Min(Screen.height-4,origin.y+68*scale+size/2+20*scale),0);
            if(editing && InventoryGui.instance) {
                var playerRect=(RectTransform)AccessTools.Field(typeof(InventoryGui),"m_player").GetValue(InventoryGui.instance);
                var corners=new Vector3[4]; playerRect.GetWorldCorners(corners);
                spacing=(corners[3].x-corners[0].x)/8; scale=spacing/vanillaBar.m_elementSpace; size=spacing-4*scale;
                Panel.position=new Vector3(corners[0].x,corners[0].y-6*scale,0);
                var container=(RectTransform)AccessTools.Field(typeof(InventoryGui),"m_container").GetValue(InventoryGui.instance);
                if(shiftedContainer!=container) { RestoreContainer(); shiftedContainer=container; containerPosition=container.anchoredPosition; }
                container.anchoredPosition=containerPosition+Vector2.down*(110*scale/Mathf.Max(.01f,container.lossyScale.y));
            } else RestoreContainer();
            Panel.sizeDelta=new Vector2(spacing*8,90*scale);
            runeFrame.anchoredPosition=new Vector2(-3,-17)*scale;
            runeFrame.sizeDelta=new Vector2(spacing*8+2*scale,size+6*scale);
            equipmentFrame.position=new Vector3(origin.x-3*scale,origin.y+3*scale,0);
            equipmentFrame.sizeDelta=new Vector2(spacing*8+2*scale,size+6*scale);
            ((RectTransform)runeFrame.Find("ActiveTab")).sizeDelta=new Vector2(26*scale,0);
            ((RectTransform)equipmentFrame.Find("ActiveTab")).sizeDelta=new Vector2(26*scale,0);
            heading.rectTransform.anchoredPosition=new Vector2(0,0); heading.fontSize=Mathf.RoundToInt(14*scale);
            runeIndicator.rectTransform.sizeDelta=equipmentIndicator.rectTransform.sizeDelta=new Vector2(26,26)*scale;
            runeIndicator.rectTransform.anchoredPosition=new Vector2(-33*scale,-20*scale-size/2+13*scale);
            equipmentIndicator.rectTransform.position=new Vector3(origin.x-33*scale,origin.y-size/2+13*scale,0);
            equipmentHeading.rectTransform.anchoredPosition=new Vector2(16,-84)*scale; equipmentHeading.fontSize=Mathf.RoundToInt(12*scale);
            equipmentHeading.gameObject.SetActive(false); equipmentIndicator.gameObject.SetActive(!editing);
            hint.rectTransform.anchoredPosition=new Vector2(0,editing?-88:-165)*scale; hint.fontSize=Mathf.RoundToInt(12*scale);
            for(int i=0;i<8;i++) {
                var rect=slotBackgrounds[i].rectTransform; rect.anchoredPosition=new Vector2(i*spacing,-20*scale); rect.sizeDelta=new Vector2(size,size);
                icons[i].rectTransform.anchoredPosition=new Vector2(6,-6)*scale; icons[i].rectTransform.sizeDelta=new Vector2(size-12*scale,size-12*scale);
                numbers[i].rectTransform.anchoredPosition=new Vector2(4,-1)*scale; numbers[i].fontSize=Mathf.RoundToInt(13*scale);
                levels[i].rectTransform.anchoredPosition=new Vector2(4,-size+15*scale); levels[i].fontSize=Mathf.RoundToInt(11*scale);
            }
        }
        private static void DestroyUI()
        {
            RestoreContainer();
            if(vanillaRect) vanillaRect.anchoredPosition=originalPosition;
            if(CanvasObject) UnityEngine.Object.Destroy(CanvasObject);
            CanvasObject=null; Panel=null; vanillaRect=null; vanillaBar=null; hover=-1;
        }
    }
    public sealed class RuneHotbarSlot : MonoBehaviour, IPointerClickHandler, IDropHandler, IPointerEnterHandler, IPointerExitHandler
    {
        internal int Slot;
        public void OnPointerClick(PointerEventData data) { RuneHotbar.Click(Slot,data.button==PointerEventData.InputButton.Right); }
        public void OnDrop(PointerEventData data) { RuneHotbar.BindDragged(Slot); }
        public void OnPointerEnter(PointerEventData data) { RuneHotbar.Hover(Slot); }
        public void OnPointerExit(PointerEventData data) { RuneHotbar.Hover(-1); }
    }
}






