using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
namespace BossMagic.Test {
 // Cooperative experimental protocol. Caster simulates projectiles; observers render only.
 // Native Character/vegetation RPCs keep health changes on the object's current owner.
 internal static class NetworkMagic {
 const string Rpc="AncientRunes.v1", StateKey="AncientRunes.state.v1";
 static ZRoutedRpc registered;
 static float nextState;
 static int sequence;
 static readonly Dictionary<ZDOID,RemoteState> states=new Dictionary<ZDOID,RemoteState>();
 static readonly Dictionary<Character,Player> chainOwners=new Dictionary<Character,Player>();
 static readonly Dictionary<long,int> received=new Dictionary<long,int>();
 internal static bool Receiving;
 internal static bool Graphics=>SystemInfo.graphicsDeviceType!=UnityEngine.Rendering.GraphicsDeviceType.Null;
 internal static void Initialize(Harmony h) { h.Patch(AccessTools.Method(typeof(ZNetScene),"Shutdown"),prefix:new HarmonyMethod(typeof(NetworkMagic),nameof(Clear))); }
 internal static ZDO Data(Character c)=>c?c.GetComponent<ZNetView>()?.GetZDO():null;
 internal static bool Owned(Character c)=>c && c.GetComponent<ZNetView>() && c.GetComponent<ZNetView>().IsOwner();
 static Character Find(ZDOID id) { var obj=ZNetScene.instance?ZNetScene.instance.FindInstance(id):null;return obj?obj.GetComponent<Character>():null; }
 internal static Player ChainOwner(Character c) { if(!Owned(c))return null; if(LightningChain.IsLinked(c)&&LightningChain.caster)return LightningChain.caster;return chainOwners.TryGetValue(c,out var p)&&p&&!p.IsDead()?p:null; }
 static void Ensure() {if(ZRoutedRpc.instance!=null && registered!=ZRoutedRpc.instance){registered=ZRoutedRpc.instance;registered.Register<ZPackage>(Rpc,Receive);received.Clear();Plugin.instance.InfoLog("ANCIENT_RUNES_NETWORK_READY v1");}}
 static void Send(int kind,Player caster,Vector3 pos,Vector3 dir,ZDOID target=default(ZDOID),float value=0,int index=0) {
 Ensure();if(Receiving || !caster || caster!=Player.m_localPlayer || registered==null || Data(caster)==null)return;
 var p=new ZPackage();p.Write(1);p.Write(++sequence);p.Write(kind);p.Write(caster.GetZDOID());p.Write(pos);p.Write(dir);p.Write(target);p.Write(value);p.Write(index);
 registered.InvokeRoutedRPC(ZRoutedRpc.Everybody,Rpc,p);
 }
 internal static void Projectile(int kind,Player caster,Vector3 pos,Vector3 dir)=>Send(kind,caster,pos,dir);
 internal static void Impact(Player caster,Vector3 pos,float radius)=>Send(4,caster,pos,Vector3.zero,value:radius);
 internal static void BeaconVisual(Player caster,Vector3 pos)=>Send(5,caster,pos,Vector3.zero);
 internal static void NatureVisual(Player caster,Vector3 pos)=>Send(6,caster,pos,Vector3.zero);
 internal static void Beacon(Player caster,Character target) {if(Owned(target))BeaconSystem.ApplyPureHit(caster,target,BeaconSystem.Damage.Value);else Send(7,caster,target.transform.position,Vector3.zero,target.GetZDOID());}
 internal static void Heal(Player caster,Character target) {if(Owned(target))NatureBlessing.ApplyHeal(target);else Send(8,caster,target.transform.position,Vector3.zero,target.GetZDOID());}
 internal static void RootHit(CageRoot root,HitData hit) {
 var attacker=hit.GetAttacker() as Player;
 // Vanilla melee is evaluated by its initiating player. Ignore damage mirrored on bystanders.
 if(attacker && attacker==Player.m_localPlayer)Send(9,attacker,root.transform.position,Vector3.zero,root.Owner.GetZDOID(),Mathf.Clamp(hit.GetTotalDamage(),0,10000),root.NetworkIndex);
 }
 static void Receive(long sender,ZPackage package) {
 try {
 if(package.Size()>512)return;
 if(package.ReadInt()!=1)return;int seq=package.ReadInt();if(received.TryGetValue(sender,out var last)&&seq<=last)return;
 int kind=package.ReadInt();var caster=Find(package.ReadZDOID()) as Player;var pos=package.ReadVector3();var dir=package.ReadVector3();var id=package.ReadZDOID();float value=package.ReadSingle();int index=package.ReadInt();
 if(!caster || caster.IsDead() || Data(caster)==null || Data(caster).GetOwner()!=sender || !Finite(pos)||!Finite(dir)||float.IsNaN(value)||float.IsInfinity(value))return;
 if(caster==Player.m_localPlayer && kind<=6)return;
 if(Vector3.Distance(caster.transform.position,pos)>130)return;
 received[sender]=seq;Receiving=true;
 if(!Graphics && kind>=1 && kind<=6)return;
 switch(kind) {
 case 1: if(dir.sqrMagnitude<.5f||dir.sqrMagnitude>1.5f)break;var obj=new GameObject("AncientRunes_RemoteMissile");obj.transform.position=pos;var missile=obj.AddComponent<LightningMissile>();missile.Replica=true;missile.Initialize(caster,dir);break;
 case 2: if(dir.sqrMagnitude>.5f&&dir.sqrMagnitude<1.5f)WindScar.Launch(caster,pos,dir).Replica=true;break;
 case 3: if(dir.sqrMagnitude>.5f&&dir.sqrMagnitude<1.5f)VineSwarm.Launch(caster,pos,dir).Replica=true;break;
 case 4: float radius=Mathf.Clamp(value,.1f,20);LightningVisual.Burst(pos,radius);ImpactEffects.Play(pos,radius);break;
 case 5: BeaconEffects.Play(pos);break;
 case 6: NatureBlessing.PlayVisual(pos);break;
 case 7: var victim=Find(id);if(Owned(victim)&&MagicSystem.IsTarget(victim,caster)&&Vector3.Distance(caster.transform.position,victim.transform.position)<=BeaconSystem.Range.Value+3)BeaconSystem.ApplyPureHit(caster,victim,BeaconSystem.Damage.Value);break;
 case 8: var ally=Find(id);if(Owned(ally)&&!ally.IsDead()&&(ally.IsTamed()||ally is Player p&&!p.IsPVPEnabled())&&Vector3.Distance(caster.transform.position,ally.transform.position)<=NatureBlessing.Radius.Value+1)NatureBlessing.ApplyHeal(ally);break;
 case 9: if(Player.m_localPlayer && id==Player.m_localPlayer.GetZDOID()) {var root=RootCage.Roots.FirstOrDefault(r=>r&&r.NetworkIndex==index);if(root&&Vector3.Distance(root.transform.position,pos)<1&&Vector3.Distance(caster.transform.position,pos)<130){var hit=new HitData();hit.m_damage.m_damage=value;root.Damage(hit);}}break;
 }
 }catch(Exception e){Plugin.instance.ErrorLog("ANCIENT_RUNES_PACKET_REJECTED "+e.Message);}finally{Receiving=false;}
 }
 static bool Finite(Vector3 p)=>!float.IsNaN(p.x)&&!float.IsNaN(p.y)&&!float.IsNaN(p.z)&&!float.IsInfinity(p.x)&&!float.IsInfinity(p.y)&&!float.IsInfinity(p.z);
 internal static void Tick() {
 Ensure(); if(!ZNetScene.instance){if(states.Count>0)Clear();return;}
 if(Time.time>=nextState) {
 nextState=Time.time+.2f;var local=Player.m_localPlayer;
 if(local&&Owned(local)) {
 var p=new ZPackage();p.Write(++sequence);p.Write(SurgeSystem.Active&&!local.IsDead());
 var links=local.IsDead()?new Character[0]:LightningChain.LinkedTargets;p.Write(links.Length);foreach(var c in links)p.Write(c.GetZDOID());
 var roots=local.IsDead()?new CageRoot[0]:RootCage.Roots.Where(r=>r&&r.Health>0).ToArray();p.Write(roots.Length);
 foreach(var root in roots){p.Write(root.NetworkIndex);p.Write(root.transform.position);p.Write(root.transform.rotation);p.Write(root.GetComponent<CapsuleCollider>().radius);p.Write(root.Health);}
 Data(local).Set(StateKey,p.GetBase64());
 }
 chainOwners.Clear();var seen=new HashSet<ZDOID>();
 foreach(var player in Player.GetAllPlayers()) {
 if(!player||player==local||player.IsDead()||Data(player)==null)continue;
 var id=player.GetZDOID();seen.Add(id);
 if(!states.TryGetValue(id,out var state)){state=new RemoteState();states[id]=state;}
 string raw=Data(player).GetString(StateKey,"");
 if(raw!=state.Last){state.Last=raw;state.Seen=Time.time;try{state.Apply(player,raw);}catch(Exception e){state.Clear();Plugin.instance.ErrorLog("ANCIENT_RUNES_STATE_REJECTED "+e.Message);}}
 if(Time.time-state.Seen>2){state.Clear();continue;}
 foreach(var target in state.Targets)if(MagicSystem.IsTarget(target,player)&&!target.IsBoss()&&Vector3.Distance(target.transform.position,player.transform.position)<LightningChain.Range.Value+2&&!chainOwners.ContainsKey(target))chainOwners[target]=player;
 }
 foreach(var key in states.Keys.Where(k=>!seen.Contains(k)).ToArray()){states[key].Clear();states.Remove(key);}
 }
 foreach(var state in states.Values)state.Draw();
 }
 internal static void Clear(){foreach(var state in states.Values)state.Clear();states.Clear();chainOwners.Clear();received.Clear();nextState=0;}
 sealed class RemoteState {
 internal string Last; internal float Seen;internal Player Owner;internal Character[] Targets=new Character[0];
 GameObject aura;readonly List<LineRenderer> lines=new List<LineRenderer>();readonly Dictionary<int,CageRoot> roots=new Dictionary<int,CageRoot>();
 internal void Apply(Player owner,string raw) {
 Owner=owner;if(string.IsNullOrEmpty(raw)||raw.Length>20000){Clear();return;}
 var p=new ZPackage(raw);p.ReadInt();bool surge=p.ReadBool();
 if(surge&&!aura&&Graphics){aura=new GameObject("AncientRunes_RemoteSurge");aura.transform.SetParent(owner.transform,false);aura.AddComponent<SurgeAura>();}else if(!surge&&aura){UnityEngine.Object.Destroy(aura);aura=null;}
 int count=p.ReadInt();if(count<0||count>12)throw new Exception("chain count");Targets=new Character[count];for(int i=0;i<count;i++)Targets[i]=Find(p.ReadZDOID());
 while(Graphics&&lines.Count<count){var go=new GameObject("AncientRunes_RemoteChain");lines.Add(LightningVisual.Line(go,17,.065f));}
 while(lines.Count>count){UnityEngine.Object.Destroy(lines[lines.Count-1].gameObject);lines.RemoveAt(lines.Count-1);}
 count=p.ReadInt();if(count<0||count>40)throw new Exception("root count");var seen=new HashSet<int>();
 for(int i=0;i<count;i++){int index=p.ReadInt();var pos=p.ReadVector3();var rotation=p.ReadQuaternion();float radius=p.ReadSingle(),health=p.ReadSingle();if(!Finite(pos)||Vector3.Distance(pos,owner.transform.position)>150||radius<.1f||radius>3||health<=0||health>2000)continue;seen.Add(index);
 roots.TryGetValue(index,out var root);if(root&&Vector3.Distance(root.transform.position,pos)>.1f){UnityEngine.Object.Destroy(root.gameObject);root=null;}if(!root){root=RootCage.CreateRoot(owner,pos,rotation,radius,health,true,index);roots[index]=root;}root.Health=health;root.transform.rotation=rotation;}
 foreach(int key in roots.Keys.Where(k=>!seen.Contains(k)).ToArray()){if(roots[key])UnityEngine.Object.Destroy(roots[key].gameObject);roots.Remove(key);}
 }
 internal void Draw(){if(!Owner)return;for(int j=0;j<Targets.Length&&j<lines.Count;j++){var line=lines[j];line.enabled=Targets[j];if(!Targets[j])continue;var start=j==0||!Targets[0]?Owner.GetCenterPoint():Targets[0].GetCenterPoint();var end=Targets[j].GetCenterPoint();for(int i=0;i<17;i++){float t=i/16f;line.SetPosition(i,Vector3.Lerp(start,end,t)+Vector3.up*Mathf.Sin(i*9+Time.time*30)*.12f*Mathf.Sin(t*Mathf.PI));}}}
 internal void Clear(){if(aura)UnityEngine.Object.Destroy(aura);aura=null;foreach(var line in lines)if(line)UnityEngine.Object.Destroy(line.gameObject);lines.Clear();foreach(var root in roots.Values)if(root)UnityEngine.Object.Destroy(root.gameObject);roots.Clear();Targets=new Character[0];}
 }
 }
}
