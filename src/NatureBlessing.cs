using System;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
namespace BossMagic.Test {
 internal static class NatureBlessing {
 internal const string RuneName="BossMagic_NatureBlessingRune";
 internal static ConfigEntry<float> Healing,Duration,Interval,Radius,Cost,Cooldown;
 internal static bool Ready;
 internal static float NextCast;
 internal static SE_Stats Native;
 private static SE_Stats template;
 private static GameObject visual,holder;
 private static Player lastPlayer;
 internal static void Initialize(Plugin host,Harmony harmony) {
 Healing=Bind(host,"TotalHealing",20,0,500,"Cura total distribuida durante o efeito, por aliado.");
 Duration=Bind(host,"Duration",4,1,60,"Duracao da cura em segundos.");
 Interval=Bind(host,"TickInterval",1,.1f,5,"Intervalo desejado entre curas; ajustado para dividir a duracao em pulsos completos.");
 Radius=Bind(host,"Radius",4.32f,1,30,"Raio da bencao no momento da ativacao, em metros.");
 Cost=Bind(host,"ActivationStamina",20,0,200,"Estamina gasta na ativacao.");
 Cooldown=Bind(host,"Cooldown",12,0,120,"Recarga em segundos.");
 PrefabManager.OnVanillaPrefabsAvailable+=Register;
 CommandManager.Instance.AddConsoleCommand(new Command());
 harmony.Patch(AccessTools.Method(typeof(Humanoid),"UseItem"),prefix:new HarmonyMethod(typeof(NatureBlessing),nameof(Use)));
 }
 private static ConfigEntry<float> Bind(Plugin host,string key,float value,float min,float max,string description)=>host.Config.Bind("BlessingOfNature",key,value,new ConfigDescription(description,new AcceptableValueRange<float>(min,max),new ConfigurationManagerAttributes { IsAdminOnly=true }));
 private static void Register() {
 var rune=new CustomItem(RuneName,"Stone",new ItemConfig {Name="Runa de Blessing of Nature",Description="Bencao do Elder: cura voce e aliados domesticados proximos ao longo do tempo."});
 var s=rune.ItemDrop.m_itemData.m_shared; s.m_itemType=ItemDrop.ItemData.ItemType.Material; s.m_maxStackSize=1; s.m_maxQuality=1; s.m_weight=.2f;
 var tex=new Texture2D(64,64,TextureFormat.RGBA32,false); var pixels=new Color[4096];
 for(int y=0;y<64;y++) for(int x=0;x<64;x++) { bool glyph=(Math.Abs(x-32)<3 && y>12 && y<52)||(Math.Abs(y-32)<3 && x>14 && x<50); pixels[y*64+x]=new Vector2(x-32,y-32).magnitude>29?Color.clear:glyph?new Color(.5f,1,1):new Color(.07f,.18f,.2f); }
 tex.SetPixels(pixels);tex.Apply();s.m_icons=new[]{Sprite.Create(tex,new Rect(0,0,64,64),new Vector2(.5f,.5f))};
 if(!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Nature rune registration failed");
 Ready=true; PrefabManager.OnVanillaPrefabsAvailable-=Register;
 }
 internal static void Prepare() {
 if(template) return;
 var shaman=PrefabManager.Instance.GetPrefab("Greydwarf_Shaman").GetComponent<Humanoid>();
 var items=(GameObject[])AccessTools.Field(typeof(Humanoid),"m_defaultItems").GetValue(shaman);
 var source=items.First(i=>i.name=="Greydwarf_shaman_heal").GetComponent<ItemDrop>().m_itemData.m_shared.m_attack.m_attackProjectile;
 var aoe=source.GetComponent<Aoe>();
 Native=ObjectDB.instance.GetStatusEffect(aoe.m_statusEffect.GetStableHashCode()) as SE_Stats;
 if(!Native) throw new InvalidOperationException("Shaman healing status missing");
 template=UnityEngine.Object.Instantiate(Native); template.name="BossMagic_NatureBlessing"; template.m_name="Blessing of Nature";
 template.m_icon=PrefabManager.Instance.GetPrefab(RuneName).GetComponent<ItemDrop>().m_itemData.GetIcon();
 holder=new GameObject("BossMagic_NatureTemplate");holder.SetActive(false);UnityEngine.Object.DontDestroyOnLoad(holder);
 visual=UnityEngine.Object.Instantiate(source,holder.transform,false);
 foreach(var script in visual.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
 foreach(var collider in visual.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
 visual.SetActive(false);
 Plugin.instance.InfoLog("BOSSMAGIC_NATURE_SOURCE "+source.name+" status="+Native.name+" total="+Native.m_healthOverTime+" ttl="+Native.m_ttl+" interval="+Native.m_healthOverTimeInterval);
 }
 private static bool Use(Humanoid __instance,Inventory inventory,ItemDrop.ItemData item) {
 if(item?.m_dropPrefab==null || item.m_dropPrefab.name!=RuneName)return true;
 var p=__instance as Player;if(p && p==Player.m_localPlayer && (inventory==null||inventory==p.GetInventory())&&p.GetInventory().ContainsItem(item))TryCast(p);return false;
 }
 internal static bool TryCast(Player player) {
 if(!player || player!=Player.m_localPlayer || !ZNet.instance)return false;
 if(player.IsDead()||player.IsTeleporting()||player.InCutscene()||player.IsAttached()||player.InAttack()||player.IsStaggering()||player.IsDodgeInvincible())return false;
 if(lastPlayer!=player){lastPlayer=player;NextCast=0;}
 if(Time.time<NextCast||!player.HaveStamina(Cost.Value)){MagicSystem.Say(player,"Blessing of Nature recarregando ou estamina insuficiente.");return false;}
 Prepare(); VineSwarm.Stop(); RootCage.Cancel();
 int count=0;
 foreach(var target in Character.GetAllCharacters().ToArray()) {
 if(!target||target.IsDead()||!(target==player||target.IsTamed()||target is Player allyPlayer && !allyPlayer.IsPVPEnabled())||Vector3.Distance(target.transform.position,player.transform.position)>Radius.Value)continue;
 NetworkMagic.Heal(player,target);count++;
 }
 PlayVisual(player.transform.position); NetworkMagic.NatureVisual(player,player.transform.position);
 player.UseStamina(Cost.Value);NextCast=Time.time+Cooldown.Value;
 Plugin.instance.InfoLog("BOSSMAGIC_NATURE_CAST targets="+count);return true;
 }
 internal static void ApplyHeal(Character target) { Prepare();
 int ticks=Mathf.Max(1,Mathf.RoundToInt(Duration.Value/Interval.Value));
 template.m_healthOverTime=Healing.Value;template.m_healthOverTimeDuration=Duration.Value;template.m_healthOverTimeInterval=Duration.Value/ticks;
 // SE_Stats uses strictly-greater interval comparisons. Allow frame rounding for the final native tick.
 template.m_ttl=Duration.Value+ticks*.05f+.1f;
 template.m_healthUpFront=0; template.m_healthPerTick=0;
 var seman=target.GetSEMan();seman.RemoveStatusEffect(template.NameHash());seman.AddStatusEffect(template);
 }
 internal static void PlayVisual(Vector3 point) { Prepare(); var fx=UnityEngine.Object.Instantiate(visual,point,Quaternion.identity);fx.transform.localScale*=Radius.Value/4.32f;fx.SetActive(true);UnityEngine.Object.Destroy(fx,3.5f); }
 internal static ItemDrop.ItemData Give(Player player) {var prefab=PrefabManager.Instance.GetPrefab(RuneName);var item=prefab.GetComponent<ItemDrop>().m_itemData.Clone();item.m_dropPrefab=prefab;return player.GetInventory().AddItem(item)?item:null;}
 private sealed class Command:ConsoleCommand {public override string Name=>"bossmagic_nature";public override string Help=>"Entrega a runa Blessing of Nature no laboratorio.";public override void Run(string[] args){if(Player.m_localPlayer&&ZNet.instance)Give(Player.m_localPlayer);}}
 }
}
