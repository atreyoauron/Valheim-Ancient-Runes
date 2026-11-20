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
    internal static class LightningChain
    {
        internal const string RuneName="BossMagic_LightningChainRune";
        internal static ConfigEntry<float> Cost, Cooldown, Range, Duration, LeashLength, PullSpeed;
        internal static bool Ready;
        internal static float NextCast;
        internal static Character Target;
        internal static Player caster;
        private static Player lastCaster;
        internal static Character[] LinkedTargets=>links.Keys.Where(c=>c).ToArray();
        private static float expires;
        private static readonly Dictionary<Character,LineRenderer> links=new Dictionary<Character,LineRenderer>();
        internal static ConfigEntry<float> ChainRadius;
        internal static ConfigEntry<int> MaxTargets;
        internal static bool IsLinked(Character target)=>target && links.ContainsKey(target);
        internal static int TargetCount=>links.Count;
        private static Material material;
        private static readonly System.Reflection.FieldInfo MoveDir=AccessTools.Field(typeof(Character),"m_moveDir");
        internal static void Initialize(Plugin host,Harmony harmony)
        {
            Cost=Bind(host,"ActivationStamina",15,0,200,"Custo inicial. Reativar para soltar nao custa estamina.");
            Cooldown=Bind(host,"Cooldown",3,0,120,"Recarga desde a ativacao.");
            Range=Bind(host,"Range",20,3,60,"Alcance da mira e distancia maxima antes de romper o vinculo.");
            Duration=Bind(host,"Duration",10,1,120,"Duracao maxima em segundos.");
            LeashLength=Bind(host,"LeashLength",3,1,10,"Comprimento livre da corrente antes de puxar.");
            PullSpeed=Bind(host,"PullSpeed",6,1,20,"Velocidade maxima de arrasto; valores provisorios.");
            ChainRadius=Bind(host,"ChainRadius",6,1,20,"Raio ao redor do PRIMEIRO alvo para capturar inimigos adicionais na ativacao.");
            MaxTargets=host.Config.Bind("LightningChain","MaxTargets",4,new ConfigDescription("Total maximo de criaturas, incluindo o primeiro alvo. 1 desativa a propagacao.",new AcceptableValueRange<int>(1,12),new ConfigurationManagerAttributes { IsAdminOnly=true }));
            PrefabManager.OnVanillaPrefabsAvailable+=Register;
            CommandManager.Instance.AddConsoleCommand(new ChainCommand());
            harmony.Patch(AccessTools.Method(typeof(Humanoid),"UseItem"),prefix:new HarmonyMethod(typeof(LightningChain),nameof(Use)));
            harmony.Patch(AccessTools.Method(typeof(Character),"UpdateMotion"),prefix:new HarmonyMethod(typeof(LightningChain),nameof(BeforeMotion)),postfix:new HarmonyMethod(typeof(LightningChain),nameof(AfterMotion)));
        }
        private static ConfigEntry<float> Bind(Plugin host,string key,float value,float min,float max,string description)
            =>host.Config.Bind("LightningChain",key,value,new ConfigDescription(description,new AcceptableValueRange<float>(min,max),new ConfigurationManagerAttributes { IsAdminOnly=true }));
        private static void Register()
        {
            var rune = new CustomItem(RuneName, "Stone", new ItemConfig { Name = "Runa de Lightning Chain", Description = "Prende o inimigo na mira e inimigos proximos com vinculos eletricos. Afaste-se para arrasta-lo; use novamente para soltar." });
            var shared = rune.ItemDrop.m_itemData.m_shared;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material; shared.m_maxStackSize = 1; shared.m_maxQuality = 1; shared.m_weight = .2f;
            var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var pixels = new Color[4096];
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) {
                float r = new Vector2(x - 32, y - 32).magnitude;
                bool bolt = Mathf.Abs(Mathf.Pow((x-24f)/10,2)+Mathf.Pow((y-25f)/15,2)-1)<.3f;
                bool ring = Mathf.Abs(Mathf.Pow((x-40f)/10,2)+Mathf.Pow((y-39f)/15,2)-1)<.3f;
                pixels[y * 64 + x] = r > 29 ? Color.clear : bolt || ring ? new Color(.9f, 1, 1) : new Color(.12f, .17f, .32f);
            }
            texture.SetPixels(pixels); texture.Apply();
            shared.m_icons = new[] { Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f)) };
            if (!ItemManager.Instance.AddItem(rune)) throw new InvalidOperationException("Lightning Chain registration failed");
            Ready = true; PrefabManager.OnVanillaPrefabsAvailable -= Register;
            Plugin.instance.InfoLog("BOSSMAGIC_CHAIN_REGISTERED");
        }
        private static bool Use(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null || item.m_dropPrefab.name != RuneName) return true;
            var player = __instance as Player;
            if (player && player == Player.m_localPlayer && (inventory == null || inventory == player.GetInventory()) && player.GetInventory().ContainsItem(item)) TryCast(player);
            return false;
        }

        internal static bool ValidTarget(Character target,Player player) => MagicSystem.IsTarget(target,player) && !target.IsBoss() && target.GetComponent<Rigidbody>() && target.GetComponent<ZNetView>();
        internal static Character FindTarget(Player player)
        {
            if(!Camera.main) return null;
            var ray=Camera.main.ViewportPointToRay(new Vector3(.5f,.5f));
            foreach(var hit in Physics.RaycastAll(ray,Range.Value+Vector3.Distance(ray.origin,player.GetEyePoint()),MagicSystem.CollisionMask,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance)) {
                var target=hit.collider.GetComponentInParent<Character>();
                if(target==player) continue;
                if(!ValidTarget(target,player) || Vector3.Distance(player.GetEyePoint(),hit.point)>Range.Value || Physics.Linecast(player.GetEyePoint(),hit.point,MagicSystem.WorldMask,QueryTriggerInteraction.Ignore)) return null;
                return target;
            }
            return null;
        }
        internal static bool TryCast(Player player)
        {
            if(!player || player!=Player.m_localPlayer || !ZNet.instance) return false;
            if(caster==player && Target) { Release(); return true; }
            if(player.IsDead() || player.InCutscene() || player.IsTeleporting() || player.IsAttached() || player.InAttack() || player.IsDodgeInvincible() || player.IsStaggering()) return false;
            if(lastCaster!=player) { NextCast=0; lastCaster=player; }
            if(Time.time<NextCast) { MagicSystem.Say(player,"Lightning Chain recarregando."); return false; }
            var target=FindTarget(player);
            if(!target) { MagicSystem.Say(player,"Mire em uma criatura hostil com caminho livre (exceto chefes)."); return false; }
            if(!player.HaveStamina(Cost.Value)) { MagicSystem.Say(player,"Estamina insuficiente."); return false; }
            if(!material) material=new Material(Shader.Find("Sprites/Default"));
            Release(); caster=player; Target=target; expires=Time.time+Duration.Value;
            foreach(var captured in SelectTargets(player,target)) {
                var line=new GameObject("BossMagic_LightningChain").AddComponent<LineRenderer>();
                line.sharedMaterial=material; line.useWorldSpace=true; line.positionCount=33; line.widthMultiplier=.065f;
                line.startColor=new Color(.3f,.85f,1,1); line.endColor=new Color(.65f,.95f,1,1);
                links.Add(captured,line);
            }
            player.UseStamina(Cost.Value); NextCast=Time.time+Cooldown.Value;
            Tick(); Plugin.instance.InfoLog("BOSSMAGIC_CHAIN_CAST target="+target.name); return true;
        }
        private static bool ClearPath(Vector3 from,Vector3 to)=>!Physics.Linecast(from,to,MagicSystem.WorldMask,QueryTriggerInteraction.Ignore);
        internal static Character[] SelectTargets(Player player,Character first)
        {
            return new[]{first}.Concat(Character.GetAllCharacters().Where(c=>c!=first && ValidTarget(c,player) &&
                Vector3.Distance(first.GetCenterPoint(),c.GetCenterPoint())<=ChainRadius.Value &&
                Vector3.Distance(player.GetCenterPoint(),c.GetCenterPoint())<=Range.Value &&
                ClearPath(first.GetCenterPoint(),c.GetCenterPoint()) && ClearPath(player.GetCenterPoint(),c.GetCenterPoint()))
                .OrderBy(c=>(c.transform.position-first.transform.position).sqrMagnitude).Take(MaxTargets.Value-1)).ToArray();
        }
        private static void RemoveLink(Character target)
        {
            if(links.TryGetValue(target,out var line) && line) UnityEngine.Object.Destroy(line.gameObject);
            links.Remove(target);
        }
        internal static void Release()
        {
            foreach(var target in links.Keys.ToArray()) RemoveLink(target);
            Target=null; caster=null;
        }
        internal static void Tick()
        {
            if(!Target || !caster) { Release(); return; }
            if(caster!=Player.m_localPlayer || caster.IsDead() || caster.IsTeleporting() || caster.InCutscene() || caster.IsAttached() || !ValidTarget(Target,caster) || Time.time>=expires || !ZNet.instance ||
                Vector3.Distance(caster.GetCenterPoint(),Target.GetCenterPoint())>Range.Value || !ClearPath(caster.GetCenterPoint(),Target.GetCenterPoint())) { Release(); return; }
            foreach(var target in links.Keys.ToArray()) {
                if(!ValidTarget(target,caster) || Vector3.Distance(caster.GetCenterPoint(),target.GetCenterPoint())>Range.Value ||
                    !ClearPath(caster.GetCenterPoint(),target.GetCenterPoint()) || !ClearPath(Target.GetCenterPoint(),target.GetCenterPoint())) { RemoveLink(target); continue; }
                Vector3 start=target==Target?caster.GetCenterPoint()+caster.transform.right*.3f:Target.GetCenterPoint(), end=target.GetCenterPoint();
                var line=links[target];
                for(int i=0;i<33;i++) {
                    float t=i/32f, envelope=Mathf.Sin(t*Mathf.PI);
                    var jitter=new Vector3(Mathf.Sin(i*9+Time.time*35),Mathf.Sin(i*13-Time.time*29),Mathf.Cos(i*7+Time.time*31))*.12f*envelope;
                    line.SetPosition(i,Vector3.Lerp(start,end,t)+jitter);
                }
            }
        }
        private static void BeforeMotion(Character __instance,ref Vector3 __state)
        {
            if(!NetworkMagic.ChainOwner(__instance)) return;
            __state=(Vector3)MoveDir.GetValue(__instance); MoveDir.SetValue(__instance,Vector3.zero);
        }
        private static void AfterMotion(Character __instance,Vector3 __state)
        {
            if(!NetworkMagic.ChainOwner(__instance)) return;
            MoveDir.SetValue(__instance,__state);
            var body=__instance.GetComponent<Rigidbody>();
            Vector3 delta=NetworkMagic.ChainOwner(__instance).transform.position-__instance.transform.position; delta.y=0;
            float speed=Mathf.Min(PullSpeed.Value,Mathf.Max(0,delta.magnitude-LeashLength.Value)*4);
            Vector3 velocity=delta.normalized*speed; velocity.y=body.linearVelocity.y;
            body.linearVelocity=velocity; // Physics resolves collisions; never teleport the creature.
        }
        internal static ItemDrop.ItemData Give(Player player)
        {
            var prefab = PrefabManager.Instance.GetPrefab(RuneName);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone(); item.m_dropPrefab = prefab;
            if (!player.GetInventory().AddItem(item)) { MagicSystem.Say(player, "Inventario cheio."); return null; }
            return item;
        }
        private sealed class ChainCommand : ConsoleCommand
        {
            public override string Name => "bossmagic_chain";
            public override string Help => "Entrega a runa Lightning Chain experimental.";
            public override void Run(string[] args) { if (Player.m_localPlayer && ZNet.instance) Give(Player.m_localPlayer); }
        }
    }
}



