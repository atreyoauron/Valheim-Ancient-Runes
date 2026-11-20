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
    internal static class WindScar
    {
        internal const string RuneName = "BossMagic_WindScarRune";
        internal static ConfigEntry<float> Slash, Woodcut, Speed, Cost, Cooldown, Range;
        internal static ConfigEntry<int> ToolTier;
        internal static bool Ready;
        internal static float NextCast;
        internal static int CastCount, Hits;
        private static Player lastCaster;
        private static Material material;
        internal static void Initialize(Plugin host, Harmony harmony)
        {
            Slash = Bind(host, "SlashDamage", 40, 0, 1000, "Dano de corte contra inimigos. Respeita as resistencias normais.");
            Woodcut = Bind(host, "WoodcutDamage", 200, 0, 5000, "Dano de corte de madeira contra arvores, troncos e tocos.");
            Speed = Bind(host, "Speed", 22, 5, 80, "Velocidade do projetil reto em m/s.");
            Range = Bind(host, "Range", 50, 5, 100, "Distancia maxima percorrida em metros.");
            Cost = Bind(host, "Stamina", 15, 0, 200, "Estamina por disparo.");
            Cooldown = Bind(host, "Cooldown", 2, .1f, 30, "Recarga em segundos.");
            ToolTier = host.Config.Bind("WindScar", "ToolTier", 3, new ConfigDescription("Nivel de ferramenta para cortar madeira; arvores acima desse requisito continuam protegidas.", new AcceptableValueRange<int>(0, 4),new ConfigurationManagerAttributes { IsAdminOnly=true }));
            PrefabManager.OnVanillaPrefabsAvailable += Register;
            CommandManager.Instance.AddConsoleCommand(new WindCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "UseItem"), prefix: new HarmonyMethod(typeof(WindScar), nameof(Use)));
        }
        private static ConfigEntry<float> Bind(Plugin host, string key, float value, float min, float max, string description)
            => host.Config.Bind("WindScar", key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max),new ConfigurationManagerAttributes { IsAdminOnly=true }));
        private static void Register()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig { Name = "Runa de Wind Scar", Description = "Lanca um X de vento em linha reta. Corta inimigos e causa grande dano a arvores, troncos e tocos. Para no primeiro obstaculo. Use na hotbar sem guardar as armas." });
            var shared = rune.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material; shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = .2f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false); var pixels = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                bool slash = (Math.Abs(x-y) < 3 || Math.Abs(x+y-63) < 3) && x > 12 && x < 52;
                pixels[y*64+x] = new Vector2(x-32,y-32).magnitude > 29 ? Color.clear : slash ? new Color(.7f,1,.85f) : new Color(.1f,.22f,.18f);
            }
            texture.SetPixels(pixels); texture.Apply(); shared.m_icons = new[] { Sprite.Create(texture,new Rect(0,0,64,64),new Vector2(.5f,.5f)) };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Wind Scar registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            Plugin.instance.InfoLog("BOSSMAGIC_WIND_REGISTERED");
        }
        private static bool Use(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != RuneName) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory()) && player.GetInventory().ContainsItem(item)) TryCast(player);
            return false;
        }
        internal static bool TryCast(Player player)
        {
            if (!ZNet.instance) return false;
            if (player.IsDead() || player.InCutscene() || player.IsTeleporting() || player.IsAttached() || player.InAttack() || player.IsDodgeInvincible() || player.IsStaggering()) return false;
            if (lastCaster != player) { NextCast = 0; lastCaster = player; }
            if (Time.time < NextCast) { MagicSystem.Say(player,"Wind Scar recarregando."); return false; }
            if (!player.HaveStamina(Cost.Value)) { MagicSystem.Say(player,"Estamina insuficiente."); return false; }
            var start = player.GetEyePoint() - Vector3.up * .3f; var direction = player.GetLookDir();
            if (Camera.main) {
                var ray = Camera.main.ViewportPointToRay(new Vector3(.5f,.5f)); var point = ray.GetPoint(Range.Value);
                foreach (var hit in Physics.RaycastAll(ray,Range.Value,MagicSystem.CollisionMask,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)) {
                    var character = hit.collider.GetComponentInParent<Character>();
                    if (character && !MagicSystem.IsTarget(character,player)) continue;
                    if (Vector3.Dot(hit.point-start,player.GetLookDir()) <= 0) continue;
                    point = hit.point; break;
                }
                direction = (point-start).normalized;
            }
            Launch(player,start,direction); NetworkMagic.Projectile(2,player,start,direction);
            player.UseStamina(Cost.Value); NextCast = Time.time + Cooldown.Value; CastCount++;
            return true;
        }
        internal static WindProjectile Launch(Player player, Vector3 point, Vector3 direction)
        {
            var obj = new GameObject("BossMagic_WindScar"); obj.transform.position = point;
            var projectile = obj.AddComponent<WindProjectile>(); projectile.Initialize(player,direction); return projectile;
        }
        internal static HitData Hit(Player owner, Vector3 point, Vector3 direction, float slash, float wood, int tier)
        {
            var hit = new HitData { m_point=point,m_dir=direction,m_toolTier=(short)tier,m_ranged=true,m_dodgeable=true,m_blockable=true,m_staggerMultiplier=0,m_backstabBonus=1 };
            hit.m_damage.m_slash=slash; hit.m_damage.m_chop=wood; hit.SetAttacker(owner); return hit;
        }
        internal static void Impact(Player owner, Collider collider, Vector3 point, Vector3 direction, float slash, float wood, int tier)
        {
            var character=collider.GetComponentInParent<Character>();
            if (character) { if(MagicSystem.IsTarget(character,owner)) { character.Damage(Hit(owner,point,direction,slash,0,tier)); Hits++; } return; }
            // Only vegetation components receive woodcut damage, never buildings or rocks.
            var tree=collider.GetComponentInParent<TreeBase>();
            if(tree) { tree.Damage(Hit(owner,point,direction,0,wood,tier)); Hits++; return; }
            var log=collider.GetComponentInParent<TreeLog>();
            if(log) { log.Damage(Hit(owner,point,direction,0,wood,tier)); Hits++; return; }
            var stump=collider.GetComponentInParent<Destructible>();
            if(stump && stump.GetDestructibleType()==DestructibleType.Tree) { stump.Damage(Hit(owner,point,direction,0,wood,tier)); Hits++; }
        }
        internal static Material GetMaterial()
        {
            if(material) return material;
            var texture=new Texture2D(128,64,TextureFormat.RGBA32,false); texture.wrapMode=TextureWrapMode.Repeat;
            var pixels=new Color[128*64];
            for(int y=0;y<64;y++) for(int x=0;x<128;x++) {
                float v=y/63f; float envelope=Mathf.Pow(Mathf.Sin(v*Mathf.PI),1.6f);
                float bands=.4f+.6f*Mathf.Pow(.5f+.5f*Mathf.Sin(v*42+x*.12f+Mathf.Sin(x*.08f)*2),3);
                pixels[y*128+x]=new Color(.8f,1,.9f,envelope*bands);
            }
            texture.SetPixels(pixels); texture.Apply();
            material=new Material(Shader.Find("Sprites/Default")) { name="BossMagic_WindStreaks",mainTexture=texture,color=new Color(.8f,1,.9f,.85f) };
            return material;
        }
        internal static ItemDrop.ItemData Give(Player player)
        {
            var prefab=PrefabManager.Instance.GetPrefab(RuneName); var item=prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab=prefab;
            if(!player.GetInventory().AddItem(item)) { MagicSystem.Say(player,"Inventario cheio."); return null; } return item;
        }
        private sealed class WindCommand : ConsoleCommand
        {
            public override string Name=>"bossmagic_wind";
            public override string Help=>"Entrega a runa Wind Scar experimental.";
            public override void Run(string[] args) { if(Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }
    public sealed class WindProjectile : MonoBehaviour
    {
        internal bool Replica;
        private Player owner; private Vector3 direction;
        private float speed,slash,wood,range,distance,age,fade=-1;
        private int tier; private readonly LineRenderer[] ribbons=new LineRenderer[6];
        internal void Initialize(Player player, Vector3 aim)
        {
            owner=player; direction=aim.normalized; speed=WindScar.Speed.Value; slash=WindScar.Slash.Value; wood=WindScar.Woodcut.Value; tier=WindScar.ToolTier.Value; range=WindScar.Range.Value;
            transform.rotation=Quaternion.LookRotation(direction);
            for(int i=0;i<ribbons.Length;i++) {
                var child=new GameObject("WindRibbon"); child.transform.SetParent(transform,false);
                var line=child.AddComponent<LineRenderer>(); ribbons[i]=line; line.useWorldSpace=false; line.positionCount=25; line.sharedMaterial=WindScar.GetMaterial();
                line.widthCurve=new AnimationCurve(new Keyframe(0,0),new Keyframe(.3f,1),new Keyframe(.65f,.8f),new Keyframe(1,0));
                line.widthMultiplier=i<2?.38f:.1f; line.textureMode=LineTextureMode.Stretch;
            }
            Destroy(gameObject,range/speed+2); Draw();
        }
        private void FixedUpdate()
        {
            if(!owner || owner.IsDead()) { Destroy(gameObject); return; }
            if(fade>=0) return;
            float step=Mathf.Min(speed*Time.fixedDeltaTime,range-distance);
            foreach(var hit in Physics.SphereCastAll(transform.position,.6f,direction,step,MagicSystem.CollisionMask,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)) {
                var character=hit.collider.GetComponentInParent<Character>();
                if(character && !MagicSystem.IsTarget(character,owner)) continue;
                if(!Replica) WindScar.Impact(owner,hit.collider,hit.point,direction,slash,wood,tier);
                transform.position+=direction*hit.distance; fade=0; return;
            }
            transform.position+=direction*step; distance+=step;
            if(distance>=range) fade=0;
        }
        private void Update()
        {
            age+=Time.deltaTime;
            if(fade>=0) { fade+=Time.deltaTime; if(fade>=.18f) { Destroy(gameObject); return; } }
            Draw();
        }
        private void Draw()
        {
            for(int i=0;i<ribbons.Length;i++) {
                if(!ribbons[i]) continue;
                float opacity=fade<0?1:1-fade/.18f;
                ribbons[i].startColor=new Color(.75f,1,.9f,opacity*(i<2?1:.5f)); ribbons[i].endColor=new Color(1,1,1,opacity*.5f);
                for(int j=0;j<25;j++) {
                    float t=j/24f*2-1; float cross=i%2==0?1:-1;
                    float ripple=Mathf.Sin(t*7+age*20+i)*.035f;
                    float offset=i<2?0:(i/2)*.12f*Mathf.Sin(t*3+age*6);
                    ribbons[i].SetPosition(j,new Vector3(t*1.05f+offset,cross*t*1.05f+ripple,-Mathf.Sin(t*Mathf.PI)*.12f-offset));
                }
            }
        }
    }
}
