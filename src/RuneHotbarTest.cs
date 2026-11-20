using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
namespace BossMagic.Test
{
    internal static class RuneHotbarTest
    {
        private static void Check(bool ok,string description) { if(!ok) throw new InvalidOperationException(description); Plugin.instance.InfoLog("BOSSMAGIC_HOTBAR_ASSERT " + description); }
        internal static void Capture(string file)
        {
            var camera=Camera.main; var oldTarget=camera.targetTexture; var oldActive=RenderTexture.active;
            var canvases=UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None).Where(c=>c.isRootCanvas && c.renderMode==RenderMode.ScreenSpaceOverlay).ToArray();
            var cameras=canvases.Select(c=>c.worldCamera).ToArray(); var distances=canvases.Select(c=>c.planeDistance).ToArray();
            var target=RenderTexture.GetTemporary(Screen.width,Screen.height,24); var pixels=new Texture2D(Screen.width,Screen.height,TextureFormat.RGB24,false);
            try {
                camera.targetTexture=target;
                foreach(var canvas in canvases) { canvas.renderMode=RenderMode.ScreenSpaceCamera; canvas.worldCamera=camera; canvas.planeDistance=1; }
                Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0); pixels.Apply();
                File.WriteAllBytes(Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath,"..","evidence",file)),pixels.EncodeToPNG());
            } finally {
                for(int i=0;i<canvases.Length;i++) { canvases[i].renderMode=RenderMode.ScreenSpaceOverlay; canvases[i].worldCamera=cameras[i]; canvases[i].planeDistance=distances[i]; }
                camera.targetTexture=oldTarget; RenderTexture.active=oldActive; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(pixels); Canvas.ForceUpdateCanvases();
            }
        }
        internal static IEnumerator Run(Player player)
        {
            var right=AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player); var left=AccessTools.Field(typeof(Humanoid),"m_leftItem").GetValue(player);
            var bag=RuneBag.Give(player); var anotherBag=RuneBag.Give(player);
            var first=WindScar.Give(player); var second=WindScar.Give(player); second.m_quality=2; // Identity fixture only: no upgrade behavior introduced.
            Check(RuneBag.Open(player,bag),"open bag for binding");
            RuneBag.Contents.MoveItemToThis(player.GetInventory(),first); RuneBag.Contents.MoveItemToThis(player.GetInventory(),second);
            first=RuneBag.Contents.GetAllItems().Single(i=>i.m_quality==1); second=RuneBag.Contents.GetAllItems().Single(i=>i.m_quality==2);
            yield return new WaitForSeconds(.4f);
            Check(RuneHotbar.CanvasObject && RuneHotbar.Panel && RuneHotbar.Panel.GetComponentsInChildren<RuneHotbarSlot>().Length==8,"eight rune slots rendered");
            var drag=AccessTools.Method(typeof(InventoryGui),"SetupDragItem");
            drag.Invoke(InventoryGui.instance,new object[]{first,RuneBag.Contents,1});
            var slot=RuneHotbar.Panel.GetComponentsInChildren<RuneHotbarSlot>().Single(s=>s.Slot==0);
            var slotRect=slot.GetComponent<RectTransform>();
            var pointer=new PointerEventData(EventSystem.current) { position=RectTransformUtility.WorldToScreenPoint(null,slotRect.TransformPoint(slotRect.rect.center)) };
            var raycastResults=new System.Collections.Generic.List<RaycastResult>(); EventSystem.current.RaycastAll(pointer,raycastResults);
            Check(raycastResults.Count>0 && raycastResults[0].gameObject.GetComponentInParent<RuneHotbarSlot>()==slot,"blue slot receives pointer above native inventory UI");
            var gridRect=((InventoryGrid)AccessTools.Field(typeof(InventoryGui),"m_playerGrid").GetValue(InventoryGui.instance)).GetComponent<RectTransform>();
            Check(!RectTransformUtility.RectangleContainsScreenPoint(gridRect,pointer.position,null),"binding bar does not cover player inventory slots");
            var playerPanel=(RectTransform)AccessTools.Field(typeof(InventoryGui),"m_player").GetValue(InventoryGui.instance);
            var containerPanel=(RectTransform)AccessTools.Field(typeof(InventoryGui),"m_container").GetValue(InventoryGui.instance);
            var playerCorners=new Vector3[4]; var containerCorners=new Vector3[4]; var barCorners=new Vector3[4];
            playerPanel.GetWorldCorners(playerCorners); containerPanel.GetWorldCorners(containerCorners); RuneHotbar.Panel.GetWorldCorners(barCorners);
            Check(barCorners[1].y<playerCorners[0].y && barCorners[0].y>containerCorners[1].y,"rune shortcuts fit between inventory and bag panels");
            ExecuteEvents.Execute<IPointerClickHandler>(slot.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
            Check(first.m_customData.ContainsKey(RuneHotbar.IdentityKey),"native dragged rune binds through slot click event");
            drag.Invoke(InventoryGui.instance,new object[]{second,RuneBag.Contents,1});
            var slot4=RuneHotbar.Panel.GetComponentsInChildren<RuneHotbarSlot>().Single(s=>s.Slot==3);
            ExecuteEvents.Execute<IDropHandler>(slot4.gameObject,new PointerEventData(EventSystem.current),ExecuteEvents.dropHandler);
            string firstId=first.m_customData[RuneHotbar.IdentityKey],secondId=second.m_customData[RuneHotbar.IdentityKey];
            Check(firstId!=secondId && RuneHotbar.Resolve(player,0)==first && RuneHotbar.Resolve(player,3)==second,"same spell different exemplars stay in separate slots");
            Check(RuneBag.Contents.GetAllItems().Count==2 && !player.GetInventory().ContainsItem(first),"binding creates a shortcut without moving or duplicating a rune");
            Check(!RuneHotbar.Toggle(player) && !RuneHotbar.RuneMode,"toggle is blocked while inventory is open");
            RuneHotbar.Refresh(); Capture("hotbar-binding.png");
            RuneBag.Close(); yield return new WaitForSeconds(.3f);
            Cursor.visible=false; Cursor.lockState=CursorLockMode.Locked;
            Check(RuneHotbar.Toggle(player) && RuneHotbar.RuneMode,"toggle switches to rune mode");
            player.AddStamina(200); WindScar.NextCast=0;
            Camera.main.transform.rotation=Quaternion.LookRotation(Vector3.up,Vector3.forward); player.SetLookDir(Vector3.up,0);
            int casts=WindScar.CastCount; var use=AccessTools.Method(typeof(Player),"UseHotbarItem");
            use.Invoke(player,new object[]{1});
            Check(WindScar.CastCount==casts+1,"native number slot routes to rune inside closed bag");
            Check(AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player)==right && AccessTools.Field(typeof(Humanoid),"m_leftItem").GetValue(player)==left,"blue hotbar never activates underlying equipment slot");
            Check(RuneHotbar.Resolve(player,0).m_customData[RuneHotbar.IdentityKey]==firstId,"identity survives bag save and reload");
            yield return new WaitForSeconds(.1f); RuneHotbar.Tick(); Capture("hotbar-active.png");
            WindScar.NextCast=0; var savedCooldown=WindScar.NextCast; float stamina=player.GetStamina(); casts=WindScar.CastCount;
            var oldDrops=UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None);
            Check(player.DropItem(player.GetInventory(),bag,1),"bound bag drops normally");
            use.Invoke(player,new object[]{1});
            Check(RuneHotbar.Resolve(player,0)==null && WindScar.CastCount==casts && player.GetStamina()==stamina && WindScar.NextCast==savedCooldown,"drop immediately blocks use without consuming stamina or cooldown");
            Check(AccessTools.Field(typeof(Humanoid),"m_rightItem").GetValue(player)==right,"missing rune never falls through to sword slot");
            RuneHotbar.Refresh(); Capture("hotbar-missing.png");
            var dropped=UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Single(d=>!oldDrops.Contains(d) && RuneBag.IsBag(d.m_itemData));
            Check(player.Pickup(dropped.gameObject,false,false),"recover original bag");
            bag=player.GetInventory().GetAllItems().Single(i=>RuneBag.IsBag(i) && RuneBag.Read(i).GetAllItems().Any(r=>r.m_customData.TryGetValue(RuneHotbar.IdentityKey,out var id) && id==firstId));
            Check(RuneHotbar.Resolve(player,0)?.m_customData[RuneHotbar.IdentityKey]==firstId,"recovering same bag reactivates exact exemplar");
            Check(RuneBag.Open(player,bag),"reopen bound bag");
            first=RuneBag.Contents.GetAllItems().Single(i=>i.m_customData[RuneHotbar.IdentityKey]==firstId);
            player.GetInventory().MoveItemToThis(RuneBag.Contents,first); RuneBag.Close();
            Check(RuneHotbar.Resolve(player,0)==null && RuneHotbar.Resolve(player,3)!=null,"removing one rune blocks only its shortcut, even with same spell still in bag");
            Check(!RuneHotbar.Cast(player,0),"possession check also rejects loose inventory rune");
            first=player.GetInventory().GetAllItems().Single(i=>i.m_customData.TryGetValue(RuneHotbar.IdentityKey,out var id) && id==firstId);
            Check(RuneBag.Open(player,anotherBag),"open another carried bag");
            RuneBag.Contents.MoveItemToThis(player.GetInventory(),first); RuneBag.Close();
            Check(RuneHotbar.Resolve(player,0)?.m_customData[RuneHotbar.IdentityKey]==firstId,"moving same exemplar between bags preserves shortcut");
            Check(RuneBag.Open(player,anotherBag),"open duplicate-identity test bag");
            var duplicate=RuneBag.Contents.GetAllItems().Single().Clone(); RuneBag.Contents.AddItem(duplicate);
            Check(RuneHotbar.Resolve(player,0)==null,"duplicate identity is unavailable rather than selecting arbitrary copy");
            RuneBag.Contents.RemoveItem(duplicate); RuneBag.Close();
            Check(RuneHotbar.Resolve(player,0)!=null,"removing duplicate restores unambiguous binding");
            string saved=player.m_customData[RuneHotbar.BindingPrefix+0];
            RuneHotbar.EnsurePlayer(null); RuneHotbar.EnsurePlayer(player);
            Check(!RuneHotbar.RuneMode && player.m_customData[RuneHotbar.BindingPrefix+0]==saved && RuneHotbar.Resolve(player,0)!=null,"character context reload restores bindings and defaults to equipment mode");
            var inventorySave=new ZPackage(); player.GetInventory().Save(inventorySave); var restored=new Inventory("RuneIdentityProbe",null,8,4); restored.Load(new ZPackage(inventorySave.GetBase64()));
            Check(restored.GetAllItems().Where(RuneBag.IsBag).SelectMany(b=>RuneBag.Read(b).GetAllItems()).Any(i=>i.m_customData.TryGetValue(RuneHotbar.IdentityKey,out var id) && id==firstId),"nested identity survives character inventory serialization");
            var stash=new Inventory("ExternalChestProbe",null,8,4); stash.MoveItemToThis(player.GetInventory(),anotherBag);
            Check(RuneHotbar.Resolve(player,0)==null,"bag transferred out of character inventory disables shortcut");
            var storedBag=stash.GetAllItems().Single(); player.GetInventory().MoveItemToThis(stash,storedBag);
            Check(RuneHotbar.Resolve(player,0)!=null,"returning bag from another inventory restores binding");
            Check(RuneBag.Open(player,bag),"open inventory to remove shortcut");
            int count=RuneBag.Contents.GetAllItems().Count;
            ExecuteEvents.Execute<IPointerClickHandler>(slot4.gameObject,new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Right},ExecuteEvents.pointerClickHandler);
            Check(RuneHotbar.Resolve(player,3)==null && RuneBag.Contents.GetAllItems().Count==count,"right click clears shortcut without deleting rune");
            RuneBag.Close(); RuneHotbar.RuneMode=false;
            yield return new WaitForSeconds(.5f);
            RuneHotbar.Tick(); Capture("hotbar-equipment.png");
            Plugin.instance.InfoLog("BOSSMAGIC_HOTBAR_PASS: UI events, individual identity, bag cast, mode routing, equipment, drop/pickup, removal, bag transfer, duplicates, serialization, character context, menu gate, clear shortcut.");
        }
    }
}

