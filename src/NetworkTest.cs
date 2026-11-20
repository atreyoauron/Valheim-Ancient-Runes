using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using UnityEngine;
namespace BossMagic.Test {
 internal static class NetworkTest {
 static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);Plugin.instance.InfoLog("NETWORK_ASSERT "+message);}
 internal static IEnumerator Run(Player local) {
 float stable=0;while(stable<3){stable=local.InCutscene()||local.IsTeleporting()?0:stable+Time.deltaTime;yield return null;}
 const long peer=87654321;
 var prefab=ZNetScene.instance.GetPrefab("Player");var position=local.transform.position+Vector3.right*3;var data=ZDOMan.instance.CreateNewZDO(position,"Player".GetStableHashCode());data.SetOwner(peer);data.Set(ZDOVars.s_health,25f);
 AccessTools.Field(typeof(ZNetView),"m_initZDO").SetValue(null,data);
 var go=UnityEngine.Object.Instantiate(prefab,position,Quaternion.identity);var remote=go.GetComponent<Player>();
 yield return new WaitForSeconds(.2f);
 var enemy=TestArea.Spawn(local.transform.position+Vector3.forward*4,false,true);enemy.SetMaxHealth(1000);enemy.SetHealth(1000);
 int seq=0;
 Action<int,ZDOID,float,int> deliver=(kind,target,value,index)=>{NetworkMagic.Data(remote).SetOwner(peer);var packet=new ZPackage();packet.Write(1);packet.Write(++seq);packet.Write(kind);packet.Write(remote.GetZDOID());packet.Write(enemy.transform.position);packet.Write(Vector3.forward);packet.Write(target);packet.Write(value);packet.Write(index);AccessTools.Method(typeof(NetworkMagic),"Receive").Invoke(null,new object[]{peer,new ZPackage(packet.GetArray())});};
 try {
 Check(NetworkMagic.Owned(enemy),"fixture creature owned locally");
 float hp=enemy.GetHealth();deliver(7,enemy.GetZDOID(),0,0);Check(enemy.GetHealth()<hp,"remote Beacon applies damage on creature owner");
 float after=enemy.GetHealth();seq--;deliver(7,enemy.GetZDOID(),0,0);Check(enemy.GetHealth()==after,"duplicate remote event ignored");
 local.SetHealth(5);deliver(8,local.GetZDOID(),0,0);Check(local.GetSEMan().GetStatusEffect("BossMagic_NatureBlessing".GetStableHashCode()),"remote Nature heals another player on its owner");
 yield return new WaitForSeconds(1.3f);Check(local.GetHealth()>5,"remote healing ticks");
 deliver(1,default(ZDOID),0,0);deliver(2,default(ZDOID),0,0);deliver(3,default(ZDOID),0,0);
 if(NetworkMagic.Graphics) {
 Check(UnityEngine.Object.FindObjectsByType<LightningMissile>(FindObjectsSortMode.None).Any(p=>p.Replica),"Thunderclap remote replica created");
 Check(UnityEngine.Object.FindObjectsByType<WindProjectile>(FindObjectsSortMode.None).Any(p=>p.Replica),"Wind Scar remote replica created");
 Check(UnityEngine.Object.FindObjectsByType<VineProjectile>(FindObjectsSortMode.None).Any(p=>p.Replica),"Vine Swarm remote replica created");
 }
 yield return new WaitForSeconds(.3f);Check(enemy.GetHealth()==after,"visual projectile replicas do not duplicate damage");
 // Serialized persistent state is the same route used by late observers and ownership changes.
 var state=new ZPackage();state.Write(1);state.Write(true);state.Write(1);state.Write(enemy.GetZDOID());state.Write(1);state.Write(0);state.Write(local.transform.position+Vector3.forward*2);state.Write(Quaternion.identity);state.Write(.7f);state.Write(80f);
 NetworkMagic.Data(remote).Set("AncientRunes.state.v1",state.GetBase64());
 yield return new WaitForSeconds(.3f);
 if(NetworkMagic.Graphics) Check(remote.GetComponentInChildren<SurgeAura>(),"remote Surge aura follows caster");
 Check(NetworkMagic.ChainOwner(enemy)==remote,"remote chain control resolves on creature owner");
 var root=UnityEngine.Object.FindObjectsByType<CageRoot>(FindObjectsSortMode.None).FirstOrDefault(c=>c.Replica&&c.Owner==remote);
 Check(root&&root.GetComponent<Collider>().enabled,"remote cage root has collision");
 NetworkMagic.Data(enemy).SetOwner(peer);Check(!NetworkMagic.ChainOwner(enemy),"non-owner never pulls creature");NetworkMagic.Data(enemy).SetOwner(ZDOMan.GetSessionID());
 var cleared=new ZPackage();cleared.Write(2);cleared.Write(false);cleared.Write(0);cleared.Write(0);NetworkMagic.Data(remote).Set("AncientRunes.state.v1",cleared.GetBase64());
 yield return new WaitForSeconds(.4f);
 Check(!remote.GetComponentInChildren<SurgeAura>()&&!root&&!NetworkMagic.ChainOwner(enemy),"remote cancellation removes aura chain and roots");
 Check(NetworkMagic.Data(remote).GetString("AncientRunes.state.v1","")==cleared.GetBase64(),"remote state not overwritten by observer");
 // Receive a player strike on a locally authoritative root, including event deduplication.
 var ownedRoot=RootCage.CreateRoot(local,enemy.transform.position,Quaternion.identity,.7f,80,false,99);RootCage.Roots.Add(ownedRoot);
 deliver(9,local.GetZDOID(),30,99);Check(Mathf.Abs(ownedRoot.Health-50)<.01f,"remote strike reduces authoritative root health");
 seq--;deliver(9,local.GetZDOID(),30,99);Check(Mathf.Abs(ownedRoot.Health-50)<.01f,"root hit deduplicated");
 deliver(9,local.GetZDOID(),60,99);Check(ownedRoot.Health<=0&&!ownedRoot.GetComponent<Collider>().enabled,"remote strike opens a cage gap");RootCage.Roots.Remove(ownedRoot);
 Plugin.instance.InfoLog("ANCIENT_RUNES_NETWORK_TEST_PASS: serialized receiver/ownership tests; not two separate machines.");
 } finally {NetworkMagic.Clear();if(enemy) {NetworkMagic.Data(enemy).SetOwner(ZDOMan.GetSessionID());ZNetScene.instance.Destroy(enemy.gameObject);}if(remote){NetworkMagic.Data(remote).SetOwner(ZDOMan.GetSessionID());ZNetScene.instance.Destroy(go);}}
 }
 }
}
