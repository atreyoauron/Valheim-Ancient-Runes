using UnityEngine;
namespace BossMagic.Test
{
    // Only instantiated by the explicitly requested gameplay integration test.
    public sealed class BeaconTestModifier : StatusEffect
    {
        public float Factor = 1;
        public override void OnDamaged(HitData hit, Character attacker) { hit.ApplyModifier(Factor); }
    }
}
