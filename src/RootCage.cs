using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace BossMagic.Test
{
    internal static class RootCage
    {
        internal const string RuneName="BossMagic_RootCageRune";
        internal static ConfigEntry<float> Radius, Range, Duration, Cost, Cooldown, Health, AttackRange, AttackDamage, AttackInterval;
        internal static bool Ready;
        internal static float NextCast;
        internal static bool Selecting=>selector;
        internal static readonly List<CageRoot> Roots=new List<CageRoot>();
        private static Player selector,owner,lastPlayer;
        private static GameObject holder,template;
        private static LineRenderer preview;
        private static Material previewMaterial;
        private static float expires;
        private static bool swallowAttack;
        internal static int SegmentCount=>Mathf.CeilToInt(2*Mathf.PI*Radius.Value/1.35f);
        internal static void Initialize(Plugin host,Harmony harmony)
        {
            Radius=Bind(host,"Radius",4,2,8,"Raio da jaula em metros.");
            Range=Bind(host,"Range",25,5,60,"Alcance de selecao.");
            Duration=Bind(host,"Duration",15,1,120,"Tempo ate as raizes desaparecerem.");
            Cost=Bind(host,"ActivationStamina",20,0,200,"Cobrada apenas ao confirmar uma area valida.");
            Cooldown=Bind(host,"Cooldown",6,0,120,"Recarga iniciada na confirmacao.");
            Health=Bind(host,"RootHealth",80,1,2000,"Vida de cada segmento destrutivel.");
            AttackRange=Bind(host,"RootAttackRange",4,1,10,"Alcance de ataque individual de cada raiz.");
            AttackDamage=Bind(host,"RootAttackDamage",12,0,200,"Dano contundente provisório por golpe de uma raiz.");
            AttackInterval=Bind(host,"RootAttackInterval",2.5f,.5f,10,"Intervalo entre ataques de cada raiz.");
            PrefabManager.OnVanillaPrefabsAvailable+=Register;
            CommandManager.Instance.AddConsoleCommand(new CageCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid),"UseItem"),prefix:new HarmonyMethod(typeof(RootCage),nameof(Use)));
            harmony.Patch(AccessTools.Method(typeof(Player),"PlayerAttackInput"),prefix:new HarmonyMethod(typeof(RootCage),nameof(AttackInput)));
        }
        private static ConfigEntry<float> Bind(Plugin host,string key,float value,float min,float max,string description)
            =>host.Config.Bind("RootCage",key,value,new ConfigDescription(description,new AcceptableValueRange<float>(min,max),new ConfigurationManagerAttributes { IsAdminOnly=true }));
        private static void Register()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig { Name = "Runa de Root Cage", Description = "Selecione uma area no chao e confirme com ataque para erguer uma jaula de raizes destrutiveis. Reutilize para cancelar a selecao." });
            var shared = rune.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material; shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = .2f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                float r = new Vector2(x - 32, y - 32).magnitude;
                bool bolt = y>18 && y<48 && (Math.Abs(x-21)<2 || Math.Abs(x-32)<2 || Math.Abs(x-43)<2);
                bool ring = Mathf.Abs(r-23)<2;
                pixels[y * 64 + x] = r > 29 ? Color.clear : bolt || ring ? new Color(.9f, 1, 1) : new Color(.12f, .17f, .32f);
            }
            texture.SetPixels(pixels); texture.Apply();
            shared.m_icons = new[] { Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f)) };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Root Cage registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            Plugin.instance.InfoLog("BOSSMAGIC_ROOTCAGE_REGISTERED");
        }
        private static bool Use(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != RuneName) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory()) && player.GetInventory().ContainsItem(item)) TryCast(player);
            return false;
        }


        private static bool Local(Player player)=>player && player==Player.m_localPlayer && ZNet.instance && !player.IsDead() && !player.IsTeleporting() && !player.InCutscene();
        internal static bool TryCast(Player player)
        {
            if(!Local(player)) return false;
            if(selector) { Cancel(); return true; }
            if(player.IsAttached() || player.InAttack() || player.IsStaggering() || player.IsDodgeInvincible()) return false;
            if(lastPlayer!=player) { NextCast=0; lastPlayer=player; }
            if(Time.time<NextCast) { MagicSystem.Say(player,"Root Cage recarregando."); return false; }
            Prepare(); selector=player;
            if(!previewMaterial) previewMaterial=new Material(Shader.Find("Sprites/Default"));
            preview=new GameObject("BossMagic_CageArea").AddComponent<LineRenderer>();
            preview.sharedMaterial=previewMaterial; preview.sortingOrder=20; preview.widthMultiplier=.10f; preview.positionCount=65; preview.useWorldSpace=true;
            MagicSystem.Say(player,"Root Cage: mire no chao. Ataque confirma; bloquear, Esc ou reutilizar cancela.");
            return true;
        }
        internal static void Prepare()
        {
            if(template) return;
            var source=PrefabManager.Instance.GetPrefab("TentaRoot");
            if(!source) throw new InvalidOperationException("Native Elder TentaRoot prefab missing");
            Plugin.instance.InfoLog("BOSSMAGIC_CAGE_SOURCE "+string.Join(",",source.GetComponentsInChildren<Component>(true).Where(c=>c).Select(c=>c.GetType().Name).Distinct()));
            holder=new GameObject("BossMagic_CageTemplates"); holder.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(holder);
            template=UnityEngine.Object.Instantiate(source,holder.transform,false);
            foreach(var script in template.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
            foreach(var collider in template.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach(var body in template.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            foreach(var audio in template.GetComponentsInChildren<AudioSource>(true)) UnityEngine.Object.DestroyImmediate(audio);
            foreach(var animator in template.GetComponentsInChildren<Animator>(true)) {
                var clips=animator.runtimeAnimatorController.animationClips;
                Plugin.instance.InfoLog("BOSSMAGIC_CAGE_CLIPS "+string.Join(",",clips.Select(c=>c.name)));
                var idle=clips.FirstOrDefault(c=>c.name.IndexOf("idle",StringComparison.OrdinalIgnoreCase)>=0);
                if(idle) idle.SampleAnimation(animator.gameObject,0);
                animator.enabled=false;
            }
            foreach(var lod in template.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(lod);
            template.name="BossMagic_ElderRootVisual"; template.SetActive(false);
        }
        internal static bool GroundArea(Player player,Vector3 candidate,out Vector3 center,out Vector3[] ring)
        {
            center=candidate; ring=null;
            if(!Local(player) || Vector3.Distance(player.GetEyePoint(),candidate)>Range.Value) return false;
            if(!Physics.Raycast(candidate+Vector3.up*.5f,Vector3.down,out var ground,2,MagicSystem.WorldMask,QueryTriggerInteraction.Ignore) || ground.normal.y<.75f || ground.collider.GetComponentInParent<CageRoot>()) return false;
            center=ground.point;
            if(Physics.Linecast(player.GetEyePoint(),center+Vector3.up*.15f,MagicSystem.WorldMask,QueryTriggerInteraction.Ignore)) return false;
            var points=new Vector3[SegmentCount];
            for(int i=0;i<SegmentCount;i++) {
                float a=i*Mathf.PI*2/SegmentCount;
                var p=center+new Vector3(Mathf.Cos(a),0,Mathf.Sin(a))*Radius.Value;
                if(!Physics.Raycast(p+Vector3.up*1.5f,Vector3.down,out var hit,3,MagicSystem.WorldMask,QueryTriggerInteraction.Ignore) || hit.normal.y<.7f || Mathf.Abs(hit.point.y-center.y)>1 || hit.collider.GetComponentInParent<CageRoot>()) return false;
                points[i]=hit.point;
            }
            ring=points; return true;
        }
        private static bool Aim(out Vector3 point,out Vector3[] ring)
        {
            point=Vector3.zero; ring=null;
            if(!selector || !Camera.main) return false;
            var ray=Camera.main.ViewportPointToRay(new Vector3(.5f,.5f));
            if(!Physics.Raycast(ray,out var hit,Range.Value+Vector3.Distance(ray.origin,selector.GetEyePoint()),MagicSystem.WorldMask,QueryTriggerInteraction.Ignore)) return false;
            point=hit.point;
            return GroundArea(selector,point,out point,out ring);
        }
        internal static bool Confirm(Vector3 candidate)
        {
            var player=selector;
            if(!GroundArea(player,candidate,out var center,out var ring) || !player.HaveStamina(Cost.Value)) { if(player) MagicSystem.Say(player,"Area invalida ou estamina insuficiente."); return false; }
            ClearCage(); owner=player; expires=Time.time+Duration.Value;
            for(int i=0;i<SegmentCount;i++) {
                var a=ring[i]; var b=ring[(i+1)%SegmentCount]; var middle=(a+b)*.5f;
                Roots.Add(CreateRoot(player,middle,Quaternion.LookRotation(Vector3.Cross((b-a).normalized,Vector3.up)),Vector3.Distance(a,b)*.49f+.02f,Health.Value,false,i));
            }
            player.UseStamina(Cost.Value); NextCast=Time.time+Cooldown.Value; Cancel();
            Plugin.instance.InfoLog("BOSSMAGIC_CAGE_CAST roots="+Roots.Count+" center="+center); return true;
        }
        internal static CageRoot CreateRoot(Player player,Vector3 position,Quaternion rotation,float radius,float health,bool replica,int index)
        {
            Prepare();
            var obj=new GameObject("BossMagic_CageRoot"); obj.layer=LayerMask.NameToLayer("static_solid");
                obj.transform.position=position; obj.transform.rotation=rotation;
                var root=obj.AddComponent<CageRoot>(); root.Health=health; root.Owner=player; root.Replica=replica; root.NetworkIndex=index;
                var body=obj.AddComponent<CapsuleCollider>(); body.center=Vector3.up*1.65f; body.height=3.3f; body.radius=radius;
                if(replica && !NetworkMagic.Graphics)return root;
                var visual=UnityEngine.Object.Instantiate(template,obj.transform,false); visual.SetActive(true);
                visual.transform.localPosition=Vector3.zero; visual.transform.localRotation=Quaternion.identity;
                visual.transform.localScale=Vector3.one;
                var bounds=new Bounds(); bool firstVertex=true;
                foreach(var skin in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                    var mesh=new Mesh(); skin.BakeMesh(mesh); skin.updateWhenOffscreen=true;
                    foreach(var vertex in mesh.vertices) {
                        var p=visual.transform.InverseTransformPoint(skin.transform.TransformPoint(vertex));
                        if(firstVertex) { bounds=new Bounds(p,Vector3.zero); firstVertex=false; } else bounds.Encapsulate(p);
                    }
                    UnityEngine.Object.Destroy(mesh);
                }
                float heightScale=3.3f/Mathf.Max(.1f,bounds.size.y);
                float widthScale=body.radius*2/Mathf.Max(.1f,Mathf.Max(bounds.size.x,bounds.size.z));
                visual.transform.localScale=new Vector3(widthScale,heightScale,widthScale);
                root.BaseOffset=new Vector3(-bounds.center.x*widthScale,-bounds.min.y*heightScale,-bounds.center.z*widthScale);
                visual.transform.localPosition=root.BaseOffset;
                root.VisualScale=visual.transform.localScale;
                root.SetupAnimation(visual.GetComponentInChildren<Animator>(true));
                root.Visual=visual.transform; return root;
        }
        internal static void Cancel() { selector=null; if(preview) UnityEngine.Object.Destroy(preview.gameObject); preview=null; }
        internal static void ClearCage() { foreach(var root in Roots.ToArray()) if(root) UnityEngine.Object.Destroy(root.gameObject); Roots.Clear(); owner=null; }
        private static bool AttackInput(Player __instance)
        {
            if(__instance!=Player.m_localPlayer) return true;
            if(!selector && !swallowAttack) return true;
            AccessTools.Field(typeof(Player),"m_queuedAttackTimer").SetValue(__instance,0f);
            AccessTools.Field(typeof(Player),"m_queuedSecondAttackTimer").SetValue(__instance,0f);
            return false;
        }
        internal static void Tick()
        {
            if(Roots.Count>0 && (!Local(owner) || Time.time>=expires)) ClearCage();
            if(swallowAttack && !ZInput.GetButton("Attack") && !ZInput.GetButton("SecondaryAttack")) swallowAttack=false;
            if(!selector) return;
            if(!Local(selector) || !RuneHotbar.CanUseInput(selector)) { Cancel(); return; }
            if(Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("Block") || ZInput.GetButtonDown("SecondaryAttack")) { Cancel(); swallowAttack=true; return; }
            bool valid=Aim(out var point,out var ring);
            if(point==Vector3.zero) { preview.enabled=false; } else {
                preview.enabled=true; preview.startColor=preview.endColor=valid?new Color(.2f,.85f,1):new Color(1,.2f,.15f);
                for(int i=0;i<65;i++) { float a=i*Mathf.PI*2/64; var p=point+new Vector3(Mathf.Cos(a),0,Mathf.Sin(a))*Radius.Value+Vector3.up*.1f; if(valid) { int index=i*SegmentCount/64%SegmentCount; p.y=ring[index].y+.1f; } preview.SetPosition(i,p); }
            }
            if(ZInput.GetButtonDown("Attack")) { swallowAttack=true; if(valid) Confirm(point); else MagicSystem.Say(selector,"Mire em uma area livre e relativamente plana dentro do alcance."); }
        }
        internal static ItemDrop.ItemData Give(Player player)
        {
            var prefab = PrefabManager.Instance.GetPrefab(RuneName);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab;
            if (!player.GetInventory().AddItem(item)) { MagicSystem.Say(player, "Inventario cheio."); return null; }
            return item;
        }
        private sealed class CageCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_cage";
            public override string Help => "Entrega a runa Root Cage experimental.";
            public override void Run(string[] args) { if (Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }
}



namespace BossMagic.Test
{
    public sealed class CageRoot:MonoBehaviour,IDestructible
    {
        internal float Health;
        internal bool Replica;
        internal int NetworkIndex;
        internal Transform Visual;
        internal Vector3 BaseOffset,VisualScale;
        internal Player Owner;
        internal Character Target;
        internal int Hits;
        private Animator animator;
        private SkinnedMeshRenderer[] skins;
        private Mesh[] animatedMeshes;
        private AnimationClip idle,attack;
        private float age,nextAttack,attackStart=-100,nextSearch;
        private bool struck;
        internal void SetupAnimation(Animator source)
        {
            animator=source;
            skins=source.GetComponentsInChildren<SkinnedMeshRenderer>(true); animatedMeshes=new Mesh[skins.Length];
            for(int i=0;i<skins.Length;i++) { var skin=skins[i]; animatedMeshes[i]=new Mesh(); skin.BakeMesh(animatedMeshes[i]); skin.gameObject.AddComponent<MeshFilter>().sharedMesh=animatedMeshes[i]; skin.gameObject.AddComponent<MeshRenderer>().sharedMaterials=skin.sharedMaterials; skin.enabled=false; }
            var clips=source.runtimeAnimatorController.animationClips;
            idle=clips.First(c=>c.name.Equals("Idle",StringComparison.OrdinalIgnoreCase));
            attack=clips.First(c=>c.name.Equals("Attack",StringComparison.OrdinalIgnoreCase));
        }
        private void BakePose()
        {
            if(Visual) { Visual.localScale=Vector3.one; Visual.localPosition=Vector3.zero; Visual.localRotation=Quaternion.identity; }
            if(skins==null) return;
            for(int i=0;i<skins.Length;i++) if(skins[i]) skins[i].BakeMesh(animatedMeshes[i]);
            if(Visual) { Visual.localScale=VisualScale; Visual.localPosition=BaseOffset+Vector3.down*(1-Mathf.Clamp01(age/.25f))*2; }
        }
        private void OnDestroy() { if(animatedMeshes!=null) foreach(var mesh in animatedMeshes) if(mesh) UnityEngine.Object.Destroy(mesh); }
        internal bool CanHit(Character target)
        {
            if(!Owner || Health<=0 || !MagicSystem.IsTarget(target,Owner) || Vector3.Distance(transform.position,target.transform.position)>RootCage.AttackRange.Value) return false;
            var from=transform.position+Vector3.up*1.3f; var to=target.GetCenterPoint();
            foreach(var hit in Physics.RaycastAll(from,(to-from).normalized,Vector3.Distance(from,to),MagicSystem.WorldMask,QueryTriggerInteraction.Ignore))
                if(hit.collider.GetComponentInParent<CageRoot>()!=this) return false;
            return true;
        }
        private void Update() { try { UpdateRoot(); } finally { BakePose(); } }
        private void UpdateRoot()
        {
            if(!Owner || Owner.IsDead() || !ZNet.instance) return;
            if(Replica && !Visual)return;
            age+=Time.deltaTime;
            if(Visual) Visual.localPosition=BaseOffset+Vector3.down*(1-Mathf.Clamp01(age/.25f))*2;
            if(Visual) Visual.localScale=Vector3.one;
            float attackTime=Time.time-attackStart;
            if(attackTime<attack.length) {
                attack.SampleAnimation(animator.gameObject,attackTime);
                if(!struck && attackTime>=attack.length*.4f) {
                    struck=true;
                    if(!Replica && CanHit(Target)) {
                        var hit=new HitData { m_point=Target.GetCenterPoint(),m_dir=(Target.transform.position-transform.position).normalized,m_blockable=true,m_dodgeable=true,m_staggerMultiplier=1 };
                        hit.m_damage.m_blunt=RootCage.AttackDamage.Value; hit.SetAttacker(Owner); Target.Damage(hit); Hits++;
                    }
                }
                return;
            }
            idle.SampleAnimation(animator.gameObject,age%Mathf.Max(.1f,idle.length));
            if(age<.4f || Time.time<nextAttack || Time.time<nextSearch) return;
            nextSearch=Time.time+.2f;
            Target=Character.GetAllCharacters().Where(CanHit).OrderBy(c=>(c.transform.position-transform.position).sqrMagnitude).FirstOrDefault();
            if(!Target) return;
            var direction=Target.transform.position-transform.position; direction.y=0;
            if(direction.sqrMagnitude>.01f) transform.rotation=Quaternion.LookRotation(direction);
            attackStart=Time.time; nextAttack=Time.time+Mathf.Max(RootCage.AttackInterval.Value,attack.length); struck=false;
        }
        public DestructibleType GetDestructibleType()=>DestructibleType.Default;
        public void Damage(HitData hit)
        {
            if(Health<=0) return;
            if(Replica) { NetworkMagic.RootHit(this,hit); return; }
            Health-=Mathf.Max(0,hit.GetTotalDamage());
            if(Health<=0) { var collider=GetComponent<Collider>(); if(collider) collider.enabled=false; enabled=false; Target=null; UnityEngine.Object.Destroy(gameObject); }
        }
    }
}






