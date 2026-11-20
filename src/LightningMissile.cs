using System.Linq;
using UnityEngine;

namespace BossMagic.Test
{
    public sealed class LightningMissile : MonoBehaviour
    {
        internal bool Replica;
        private Player owner;
        private Vector3 direction, velocity;
        private float speed, damage, radius, push, distance, gravity;
        private bool impacted;
        internal float FlightTime;
        private LineRenderer arc;
        private Light lightSource;

        internal void Initialize(Player caster, Vector3 aim, MagicSystem.TrajectoryMode? trajectory = null)
        {
            owner = caster; direction = aim.normalized;
            speed = MagicSystem.Speed.Value; damage = MagicSystem.Damage.Value;
            radius = MagicSystem.Radius.Value; push = MagicSystem.Push.Value;
            velocity = direction * speed;
            gravity = (trajectory ?? MagicSystem.Trajectory.Value) == MagicSystem.TrajectoryMode.Arrow ? MagicSystem.Gravity.Value : 0f;
            arc = LightningVisual.Line(gameObject, 9, 0.11f);
            var trail = gameObject.AddComponent<TrailRenderer>();
            trail.sharedMaterial = MagicSystem.GetGlow(); trail.time = 0.18f;
            trail.startWidth = 0.14f; trail.endWidth = 0f; trail.minVertexDistance = 0.08f;
            trail.startColor = Color.white; trail.endColor = new Color(0.1f, 0.5f, 1f, 0f);
            lightSource = gameObject.AddComponent<Light>();
            lightSource.color = new Color(0.2f, 0.65f, 1f); lightSource.range = 4f; lightSource.intensity = 2f;
            Destroy(gameObject, 12f);
        }

        private void FixedUpdate()
        {
            if (!owner || owner.IsDead()) { Destroy(gameObject); return; }
            if (impacted) return;
            float dt = Time.fixedDeltaTime;
            FlightTime += dt;
            Vector3 displacement = velocity * dt + Vector3.down * (0.5f * gravity * dt * dt);
            velocity += Vector3.down * (gravity * dt);
            float step = displacement.magnitude;
            direction = displacement.normalized;
            var hits = Physics.SphereCastAll(transform.position, 0.12f, direction, step, MagicSystem.CollisionMask, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits.OrderBy(h => h.distance)) {
                var character = hit.collider.GetComponentInParent<Character>();
                if (character && !MagicSystem.IsTarget(character, owner)) continue;
                impacted = true;
                if(!Replica) MagicSystem.Impact(owner, hit.point, hit.normal.sqrMagnitude > 0.1f ? hit.normal : Vector3.up, damage, radius, push);
                Destroy(gameObject); return;
            }
            transform.position += direction * step;
            distance += step;
            if (distance >= 60f) Destroy(gameObject); // Misses expire without damage.
        }

        private void Update()
        {
            if (!arc) return;
            Vector3 right = Vector3.Cross(direction, Vector3.up).normalized;
            if (right.sqrMagnitude < 0.1f) right = Vector3.right;
            Vector3 up = Vector3.Cross(right, direction).normalized;
            for (int i = 0; i < 9; i++) {
                float offset = Mathf.Sin(Time.time * 80f + i * 2.3f) * 0.13f;
                arc.SetPosition(i, transform.position - direction * (i * 0.15f) + right * offset + up * Mathf.Cos(Time.time * 65f + i * 1.7f) * 0.08f);
            }
        }
    }

    public sealed class LightningVisual : MonoBehaviour
    {
        private LineRenderer[] rays;
        private LineRenderer ring;
        private Light flash;
        private float age, radius;
        internal static LineRenderer Line(GameObject go, int points, float width)
        {
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = MagicSystem.GetGlow(); line.useWorldSpace = true;
            line.positionCount = points; line.startWidth = width; line.endWidth = width * 0.45f;
            line.startColor = Color.white; line.endColor = new Color(0.15f, 0.6f, 1f, 0.7f);
            return line;
        }
        internal static void Burst(Vector3 position, float radius)
        {
            var go = new GameObject("BossMagic_ElectricImpact"); go.transform.position = position;
            var burst = go.AddComponent<LightningVisual>(); burst.radius = radius;
            burst.ring = Line(go, 49, 0.12f);
            burst.rays = new LineRenderer[10];
            for (int i = 0; i < burst.rays.Length; i++) {
                var child = new GameObject("Arc"); child.transform.SetParent(go.transform);
                burst.rays[i] = Line(child, 5, 0.09f);
            }
            burst.flash = go.AddComponent<Light>(); burst.flash.color = new Color(0.2f, 0.65f, 1f);
            burst.flash.range = radius * 3f;
            Destroy(go, 0.65f);
        }
        private void Update()
        {
            age += Time.deltaTime;
            float fade = Mathf.Clamp01(1f - age / 0.65f);
            float size = radius * Mathf.Clamp01(age / 0.18f);
            Vector3 center = transform.position;
            for (int i = 0; i < 49; i++) {
                float angle = i * Mathf.PI * 2f / 48f;
                ring.SetPosition(i, center + new Vector3(Mathf.Cos(angle), 0.04f, Mathf.Sin(angle)) * size);
            }
            ring.widthMultiplier = 0.12f * fade;
            for (int r = 0; r < rays.Length; r++) {
                float angle = r * Mathf.PI * 2f / rays.Length;
                Vector3 direction = new Vector3(Mathf.Cos(angle), 0.15f, Mathf.Sin(angle));
                for (int i = 0; i < 5; i++) rays[r].SetPosition(i, center + direction * (size * i / 4f) + Vector3.up * Mathf.Sin(i * 2f + age * 90f + r) * 0.15f);
                rays[r].widthMultiplier = 0.09f * fade;
            }
            flash.intensity = fade * 5f;
        }
    }
}


