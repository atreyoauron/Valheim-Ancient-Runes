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
    internal static class VineSwarm
    {
        internal const string RuneName="BossMagic_VineSwarmRune";
        internal static ConfigEntry<float> Damage, Speed, Range, Cost, Cooldown, Duration, Interval, Spread;
        internal static bool Ready;
        internal static float NextCast;
        internal static int Shots,Hits;
        internal static Player Caster;
        internal static bool Active=>Caster;
        private static Player lastCaster;
        private static float until,nextShot;
        private static Transform hand;
        private static GameObject holder,visualTemplate;
        private static readonly System.Reflection.FieldInfo MoveDir=AccessTools.Field(typeof(Character),"m_moveDir");
        internal static void Initialize(Plugin host,Harmony harmony)
        {
            Damage=Bind(host,"PierceDamage",8,0,500,"Dano perfurante por vinha; provisório.");
            Speed=Bind(host,"ProjectileSpeed",24,5,60,"Velocidade das vinhas em metros por segundo.");
            Range=Bind(host,"Range",35,5,80,"Distancia maxima dos projeteis.");
            Cost=Bind(host,"ActivationStamina",20,0,200,"Custo inicial da canalizacao.");
            Cooldown=Bind(host,"Cooldown",6,0,120,"Recarga contada desde a ativacao.");
            Duration=Bind(host,"ChannelDuration",3,.5f,10,"Duracao da rajada; sem movimento voluntario durante a canalizacao.");
            Interval=Bind(host,"ShotInterval",.2f,.08f,1,"Intervalo entre vinhas.");
            Spread=Bind(host,"SpreadDegrees",8,0,45,"Desvio angular maximo em graus ao redor da mira, sorteado por vinha. Zero desativa a dispersao. Aplicado imediatamente aos proximos disparos.");
            PrefabManager.OnVanillaPrefabsAvailable+=Register;
            CommandManager.Instance.AddConsoleCommand(new VinesCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid),"UseItem"),prefix:new HarmonyMethod(typeof(VineSwarm),nameof(Use)));
            harmony.Patch(AccessTools.Method(typeof(Player),"SetControls"),prefix:new HarmonyMethod(typeof(VineSwarm),nameof(Controls)));
            harmony.Patch(AccessTools.Method(typeof(Character),"UpdateMotion"),prefix:new HarmonyMethod(typeof(VineSwarm),nameof(Motion)));
            harmony.Patch(AccessTools.Method(typeof(Player),"PlayerAttackInput"),prefix:new HarmonyMethod(typeof(VineSwarm),nameof(AttackInput)));
        }
        private static ConfigEntry<float> Bind(Plugin host,string key,float value,float min,float max,string description)
            =>host.Config.Bind("VineSwarm",key,value,new ConfigDescription(description,new AcceptableValueRange<float>(min,max),new ConfigurationManagerAttributes { IsAdminOnly=true }));
        private static void Register()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig { Name = "Runa de Vine Swarm", Description = "Canaliza uma rajada de vinhas do Elder. Movimento bloqueado durante o disparo; reutilize ou bloqueie para cancelar." });
            var shared = rune.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material; shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = .2f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false); var pixels = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                bool slash = y>10 && y<54 && (Math.Abs(x-(20+6*Mathf.Sin(y*.15f)))<2 || Math.Abs(x-(40+6*Mathf.Sin(y*.15f)))<2);
                pixels[y*64+x] = new Vector2(x-32,y-32).magnitude > 29 ? Color.clear : slash ? new Color(.7f,1,.85f) : new Color(.1f,.22f,.18f);
            }
            texture.SetPixels(pixels); texture.Apply(); shared.m_icons = new[] { Sprite.Create(texture,new Rect(0,0,64,64),new Vector2(.5f,.5f)) };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Vine Swarm registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            Plugin.instance.InfoLog("BOSSMAGIC_VINES_REGISTERED");
        }
        private static bool Use(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != RuneName) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory()) && player.GetInventory().ContainsItem(item)) TryCast(player);
            return false;
        }

        internal static void Prepare()
        {
            if(visualTemplate) return;
            var boss=PrefabManager.Instance.GetPrefab("gd_king").GetComponent<Humanoid>();
            var items=(GameObject[])AccessTools.Field(typeof(Humanoid),"m_defaultItems").GetValue(boss);
            foreach(var item in items) { var a=item.GetComponent<ItemDrop>().m_itemData.m_shared.m_attack; Plugin.instance.InfoLog("BOSSMAGIC_ELDER_ATTACK "+item.name+" projectile="+(a.m_attackProjectile?a.m_attackProjectile.name:"none")+" bursts="+a.m_projectileBursts); }
            var attack=items.Select(i=>i.GetComponent<ItemDrop>().m_itemData.m_shared.m_attack).Where(a=>a.m_attackProjectile && a.m_attackProjectile.GetComponent<Projectile>()).OrderByDescending(a=>a.m_projectileBursts).First();
            var source=attack.m_attackProjectile;
            Plugin.instance.InfoLog("BOSSMAGIC_VINES_SOURCE "+source.name+" components="+string.Join(",",source.GetComponentsInChildren<Component>(true).Where(c=>c).Select(c=>c.GetType().Name).Distinct()));
            holder=new GameObject("BossMagic_VineTemplates"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            visualTemplate=UnityEngine.Object.Instantiate(source,holder.transform,false);
            foreach(var script in visualTemplate.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
            foreach(var collider in visualTemplate.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach(var body in visualTemplate.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            visualTemplate.SetActive(false);
        }
        internal static bool TryCast(Player player)
        {
            if(!player || player!=Player.m_localPlayer || !ZNet.instance) return false;
            if(Caster==player) { Stop(); return true; }
            if(player.IsDead() || player.IsTeleporting() || player.InCutscene() || player.IsAttached() || player.InAttack() || player.IsStaggering() || player.IsDodgeInvincible()) return false;
            if(lastCaster!=player) { NextCast=0; lastCaster=player; }
            if(Time.time<NextCast || !player.HaveStamina(Cost.Value)) { MagicSystem.Say(player,"Vine Swarm recarregando ou estamina insuficiente."); return false; }
            Prepare(); RootCage.Cancel(); Caster=player;
            hand=player.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name.Equals("RightHand",StringComparison.OrdinalIgnoreCase));
            Plugin.instance.InfoLog("BOSSMAGIC_VINES_CHANNEL hand="+(hand?hand.name:"eye fallback"));
            until=Time.time+Duration.Value; nextShot=Time.time;
            MoveDir.SetValue(player,Vector3.zero);
            var body=player.GetComponent<Rigidbody>(); var velocity=body.linearVelocity; velocity.x=velocity.z=0; body.linearVelocity=velocity;
            player.UseStamina(Cost.Value); NextCast=Time.time+Cooldown.Value;
            MagicSystem.Say(player,"Vine Swarm: canalizando. Reutilize ou bloqueie para cancelar."); return true;
        }
        internal static void Stop() { if(Caster) MoveDir.SetValue(Caster,Vector3.zero); Caster=null; hand=null; }
        private static void Controls(Player __instance,ref Vector3 movedir,ref bool attack,ref bool attackHold,ref bool secondaryAttack,ref bool secondaryAttackHold,bool block,bool blockHold,ref bool jump,ref bool crouch,ref bool run,ref bool autoRun,ref bool dodge)
        {
            if(__instance!=Caster) return;
            if(block || blockHold || secondaryAttack) { Stop(); return; }
            movedir=Vector3.zero; attack=attackHold=secondaryAttack=secondaryAttackHold=jump=crouch=run=autoRun=dodge=false;
        }
        private static void Motion(Character __instance) { if(__instance==Caster) MoveDir.SetValue(__instance,Vector3.zero); }
        private static bool AttackInput(Player __instance)
        {
            if(__instance!=Caster) return true;
            AccessTools.Field(typeof(Player),"m_queuedAttackTimer").SetValue(__instance,0f); AccessTools.Field(typeof(Player),"m_queuedSecondAttackTimer").SetValue(__instance,0f); return false;
        }
        internal static void Tick()
        {
            if(!Caster) return;
            if(Caster!=Player.m_localPlayer || !RuneHotbar.CanUseInput(Caster) || Caster.IsStaggering() || Caster.IsAttached() || Caster.IsDodgeInvincible() || Time.time>=until) { Stop(); return; }
            if(Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("Block") || ZInput.GetButtonDown("SecondaryAttack")) { Stop(); return; }
            if(Time.time<nextShot) return;
            nextShot=Time.time+Interval.Value;
            var start=hand?hand.position:Caster.GetEyePoint()-Vector3.up*.3f;
            var direction=Caster.GetLookDir();
            if(Camera.main) {
                var ray=Camera.main.ViewportPointToRay(new Vector3(.5f,.5f)); var point=ray.GetPoint(Range.Value);
                foreach(var hit in Physics.RaycastAll(ray,Range.Value,MagicSystem.CollisionMask,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)) {
                    var target=hit.collider.GetComponentInParent<Character>();
                    if(target && !MagicSystem.IsTarget(target,Caster)) continue;
                    if(Vector3.Dot(hit.point-start,Caster.GetLookDir())<=0) continue;
                    point=hit.point; break;
                }
                direction=(point-start).normalized;
            }
            direction=ApplySpread(direction); Launch(Caster,start,direction); NetworkMagic.Projectile(3,Caster,start,direction); Shots++;
        }
        private static Vector3 ApplySpread(Vector3 direction)
        {
            if(Spread.Value<=0) return direction;
            // Uniform solid-angle sampling inside a cone centered on the current aim.
            float cosTheta=Mathf.Lerp(1f,Mathf.Cos(Spread.Value*Mathf.Deg2Rad),UnityEngine.Random.value);
            float sinTheta=Mathf.Sqrt(Mathf.Max(0,1f-cosTheta*cosTheta));
            float phi=UnityEngine.Random.value*2f*Mathf.PI;
            var local=new Vector3(sinTheta*Mathf.Cos(phi),sinTheta*Mathf.Sin(phi),cosTheta);
            return Quaternion.FromToRotation(Vector3.forward,direction.normalized)*local;
        }
        internal static VineProjectile Launch(Player player,Vector3 start,Vector3 direction)
        {
            Prepare(); var obj=new GameObject("BossMagic_VineProjectile"); obj.transform.position=start; obj.transform.rotation=Quaternion.LookRotation(direction);
            var visual=UnityEngine.Object.Instantiate(visualTemplate,obj.transform,false); visual.transform.localPosition=Vector3.zero; visual.transform.localRotation=Quaternion.identity; visual.transform.localScale*=.35f;
            foreach(var trail in visual.GetComponentsInChildren<TrailRenderer>(true)) { trail.widthMultiplier*=.35f; trail.time=Mathf.Min(trail.time,.35f); }
            visual.SetActive(true);
            var projectile=obj.AddComponent<VineProjectile>(); projectile.Initialize(player,direction); return projectile;
        }
        internal static ItemDrop.ItemData Give(Player player)
        {
            var prefab=PrefabManager.Instance.GetPrefab(RuneName); var item=prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab=prefab;
            if(!player.GetInventory().AddItem(item)) { MagicSystem.Say(player,"Inventario cheio."); return null; } return item;
        }
        private sealed class VinesCommand : ConsoleCommand
        {
            public override string Name=>"bossmagic_vines";
            public override string Help=>"Entrega a runa Vine Swarm experimental.";
            public override void Run(string[] args) { if(Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }

    public sealed class VineProjectile:MonoBehaviour
    {
        internal bool Replica;
        private Player owner;
        private Vector3 direction;
        private float distance,speed,range,damage;
        internal void Initialize(Player player,Vector3 aim) { owner=player; direction=aim.normalized; speed=VineSwarm.Speed.Value; range=VineSwarm.Range.Value; damage=VineSwarm.Damage.Value; Destroy(gameObject,range/speed+1); }
        private void FixedUpdate()
        {
            if(!owner || owner.IsDead()) { Destroy(gameObject); return; }
            float step=Mathf.Min(speed*Time.fixedDeltaTime,range-distance);
            foreach(var hit in Physics.SphereCastAll(transform.position,.15f,direction,step,MagicSystem.CollisionMask,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)) {
                var target=hit.collider.GetComponentInParent<Character>();
                if(target && !MagicSystem.IsTarget(target,owner)) continue;
                if(target && !Replica) { var damageHit=new HitData { m_point=hit.point,m_dir=direction,m_ranged=true,m_blockable=true,m_dodgeable=true,m_staggerMultiplier=0,m_backstabBonus=1 }; damageHit.m_damage.m_pierce=damage; damageHit.SetAttacker(owner); target.Damage(damageHit); VineSwarm.Hits++; }
                Destroy(gameObject); return;
            }
            transform.position+=direction*step; distance+=step; if(distance>=range) Destroy(gameObject);
        }
    }
}
