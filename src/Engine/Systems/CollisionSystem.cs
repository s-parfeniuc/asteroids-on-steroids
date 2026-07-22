using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Diagnostics;
using AsteroidsEngine.Engine.Events;

namespace AsteroidsEngine.Engine.Systems;

/// <summary>
/// Detects and resolves collisions between entities with Transform + Collider.
///
/// Each frame:
///   1. Query the shared spatial index (built this frame by BroadPhaseSystem).
///   2. Narrow phase per candidate pair → separate overlap, publish CollisionEvent,
///      and gather a contact for every pair where both bodies are dynamic.
///   3. Iterative velocity solve (sequential impulses: normal + Coulomb friction).
///      Iterating lets coupled/stacked contacts converge instead of jittering.
///   4. Sleeping: bodies at rest below the velocity thresholds deactivate and are
///      skipped by integration + the solver until a contact or force wakes them.
///
/// A pair (A, B) is tested only if (A.Mask & B.Layer) != 0. Each pair once per frame.
/// </summary>
public sealed class CollisionSystem : ISystem
{
    private readonly ISpatialIndex _spatial;
    private readonly EventBus      _bus;

    private readonly List<Entity>        _candidates  = new();
    private readonly HashSet<(int, int)> _testedPairs = new();
    private readonly List<Contact>       _contacts    = new();
    private readonly List<ContactInfo>   _hitBuf      = new();

    public bool ResolveOverlap { get; set; } = true;

    /// <summary>Sequential-impulse velocity iterations. More = stabler stacks.</summary>
    public int Iterations { get; set; } = 6;

    /// <summary>Enable body sleeping (deactivation of resting bodies).</summary>
    public bool EnableSleeping { get; set; } = true;

    // Sleep thresholds.
    private const float LinSleepTol   = 6f;     // px/s
    private const float AngSleepTol   = 0.12f;  // rad/s
    private const float SleepTime     = 0.5f;   // s below tolerance before sleeping
    private const float RestitutionVelThreshold = 30f; // below this approach speed, no bounce
    private const float PenetrationSlop   = 0.5f;      // allowed overlap (px) before correcting
    private const float CorrectionPercent = 0.4f;      // fraction of penetration fixed per frame
    private const float MaxCorrection     = 16f;        // cap on positional push per frame (px): deep
                                                       // overlaps separate over several frames, never teleport
    private const float DeepNoBounce      = 100000f;        // overlap (px) past which we skip restitution — a body
                                                       // that appears already-embedded should ooze out, not bounce

    public CollisionSystem(ISpatialIndex spatial, EventBus bus)
    {
        _spatial = spatial;
        _bus     = bus;
    }

    public void Update(World world, double dt)
    {
        _testedPairs.Clear();
        _contacts.Clear();

        // Phase 1 (index build) is done once per frame by BroadPhaseSystem into the shared _spatial.

        // --- Phase 2 & 3: narrow phase → separate, publish, gather contacts ---
        world.ForEach<Transform, Collider>((Entity entityA, ref Transform tA, ref Collider cA) =>
        {
            if (world.HasComponent<DisabledTag>(entityA)) return;

            var (min, max) = cA.Shape.GetAABB(tA.Position, tA.Rotation);
            _candidates.Clear();
            _spatial.GetCandidates(min, max, _candidates);

            foreach (var entityB in _candidates)
            {
                if (entityB == entityA) continue;

                int idA = entityA.Id, idB = entityB.Id;
                var pair = idA < idB ? (idA, idB) : (idB, idA);
                if (!_testedPairs.Add(pair)) continue;

                if (!world.IsAlive(entityB)) continue;
                ref var cB = ref world.GetComponent<Collider>(entityB);

                if ((cA.Mask & cB.Layer) == 0 && (cB.Mask & cA.Layer) == 0) continue;

                ref var tB = ref world.GetComponent<Transform>(entityB);

                // Collect a contact manifold (multiple points for compound bodies).
                _hitBuf.Clear();
                CollectPairContacts(cA.Shape, tA.Position, tA.Rotation,
                                    cB.Shape, tB.Position, tB.Rotation, _hitBuf);
                if (_hitBuf.Count == 0) continue;

                // Normals are already oriented B→A per-contact — for compounds by the
                // contacting cell's geometry (CollectPairContacts), which is what makes
                // concave craters correct. No whole-body re-orientation here (that flipped
                // crater-wall normals inward and shoved bodies through the asteroid).

                // Deepest contact drives positional correction + the gameplay event.
                ContactInfo deepest = _hitBuf[0];
                for (int k = 1; k < _hitBuf.Count; k++)
                    if (_hitBuf[k].Depth > deepest.Depth) deepest = _hitBuf[k];

                // Sensors (e.g. the piercing round) are detected + reported but never resolved:
                // no separation, no contact constraints → no impulse. They pass through.
                bool sensor = cA.Sensor || cB.Sensor;

                if (ResolveOverlap && !sensor)
                {
                    TrySeparate(world, entityA, ref tA, entityB, ref tB, deepest);
                    for (int k = 0; k < _hitBuf.Count; k++)
                        GatherContact(world, entityA, entityB, _hitBuf[k]);
                }

                // Capture the PRE-solve normal closing speed at the deepest contact (Phase 4 below
                // mutates velocities) so fracture energy uses the true approach, not the bounce.
                float approach = ApproachNormalSpeed(world, entityA, entityB, tA.Position, tB.Position, deepest);
                _bus.Publish(new CollisionEvent(entityA, entityB, deepest, approach));
            }
        });

        // --- Phase 4: iterative velocity solve ---
        for (int it = 0; it < Iterations; it++)
            for (int i = 0; i < _contacts.Count; i++)
            {
                var c = _contacts[i];
                SolveContact(world, ref c);
                _contacts[i] = c;
            }

        // --- Net contact impulse (after convergence): one line per contact, not per iteration ---
        if (ForceLog.Enabled && (ForceLog.Categories & ForceCat.Contact) != 0)
            foreach (var c in _contacts)
            {
                if (!ForceLog.On(ForceCat.Contact, c.A.Id) && !ForceLog.On(ForceCat.Contact, c.B.Id)) continue;
                Vector2 p = c.AccumN * c.Normal + c.AccumT * c.Tangent;   // total impulse on A
                ForceLog.Write(ForceCat.Contact, c.A.Id,
                    $"vs e{c.B.Id}: Jn{c.AccumN:0.#}·n{ForceLog.V(c.Normal)} + Jt{c.AccumT:0.#}·t = P{ForceLog.V(p)}  " +
                    $"→ ΔvA {ForceLog.V(p * c.InvMassA)} ΔωA {c.InvIA * Cross(c.rA, p):0.###} ; " +
                    $"ΔvB {ForceLog.V(-p * c.InvMassB)} ΔωB {-c.InvIB * Cross(c.rB, p):0.###}");
            }

        // --- Phase 5: sleeping ---
        if (EnableSleeping) UpdateSleep(world, (float)dt);
    }

    /// <summary>
    /// Pushes overlapping entities apart along the contact normal, weighted by mass.
    /// Skipped if either entity has no RigidBody. (Positional correction; the
    /// velocity solver handles the bounce/friction response.)
    /// </summary>
    private static void TrySeparate(World world,
                                     Entity eA, ref Transform tA,
                                     Entity eB, ref Transform tB,
                                     ContactInfo contact)
    {
        if (!world.HasComponent<RigidBody>(eA) || !world.HasComponent<RigidBody>(eB)) return;

        // Fracture siblings (just detached from the same body) share a FractureGroup id.
        // Suppressing positional correction between them prevents a freshly-detached piece
        // from pushing its former parent in the wrong direction. Velocity solving is unchanged.
        if (world.HasComponent<FractureGroup>(eA) && world.HasComponent<FractureGroup>(eB) &&
            world.GetComponent<FractureGroup>(eA).Id == world.GetComponent<FractureGroup>(eB).Id)
            return;

        float massA  = world.GetComponent<RigidBody>(eA).Mass;
        float massB  = world.GetComponent<RigidBody>(eB).Mass;
        float total  = massA + massB;
        float shareA = total > 0f ? massB / total : 0.5f;

        // Gentle Baumgarte-style correction: fix only a fraction of the penetration
        // beyond a small slop, so deep multi-cell overlaps resolve over a few frames
        // instead of teleporting (which flings bodies and spins them up).
        float corr = MathF.Max(0f, contact.Depth - PenetrationSlop) * CorrectionPercent;
        if (corr <= 0f) return;
        corr = MathF.Min(corr, MaxCorrection);   // deep overlaps ooze apart over frames, never teleport
        var correction = contact.Normal * corr;
        if (ForceLog.On(ForceCat.Separation, eA.Id) || ForceLog.On(ForceCat.Separation, eB.Id))
            ForceLog.Write(ForceCat.Separation, eA.Id,
                $"vs e{eB.Id}: depth{contact.Depth:0.##} → push min((depth-slop{PenetrationSlop})·{CorrectionPercent}, max{MaxCorrection}) = {corr:0.##}px " +
                $"along n{ForceLog.V(contact.Normal)} ; shareA {shareA:0.##}");
        tA.Position += correction *  shareA;
        tB.Position -= correction * (1f - shareA);
    }

    /// <summary>
    /// Builds the contact set for a pair. Compound shapes yield a manifold (one
    /// contact per overlapping part); simple shapes yield a single contact. All
    /// normals point from B into A.
    /// </summary>
    private static void CollectPairContacts(
        CollisionShape sa, Vector2 pa, float ra,
        CollisionShape sb, Vector2 pb, float rb, List<ContactInfo> outList)
    {
        // CollectContacts iterates one body's cells and orients each contact normal so
        // it points from that cell outward toward the other body. Convention here: B→A.
        //
        // When both shapes are compounds (the common case: all asteroid bodies, all
        // fragment bodies) we MUST use B's cells as the reference — B's cell normals
        // point toward A, giving correct push direction for A. Using A's cells and
        // then flipping (the old approach) produces the exact opposite for bodies
        // sitting inside concave craters because the fragment's own cell centroid
        // is on the wrong side relative to the asteroid's crater walls.
        if (sa is CompoundShape ca && sb is CompoundShape cb)
        {
            // Compound vs compound: collect from whichever has more cells — that body
            // is most likely the concave one whose cell-outward normals are authoritative.
            // For equal counts (fragment vs fragment) it doesn't matter which we use.
            // CollectContacts always labels its own cells "PartA", so when B is the reference
            // body the indices come back with the roles reversed and must be swapped back.
            if (ca.PartCount >= cb.PartCount)
            {
                int start = outList.Count;
                ca.CollectContacts(pa, ra, sb, pb, rb, outList);       // A-cell → B
                for (int i = start; i < outList.Count; i++) outList[i] = outList[i].Flipped();  // → B→A
            }
            else
            {
                int start = outList.Count;
                cb.CollectContacts(pb, rb, sa, pa, ra, outList);       // B-cell → A
                for (int i = start; i < outList.Count; i++) outList[i] = outList[i].SwappedParts();
            }
        }
        else if (sa is CompoundShape ca2)
        {
            int start = outList.Count;
            ca2.CollectContacts(pa, ra, sb, pb, rb, outList);          // A-cell → B
            for (int i = start; i < outList.Count; i++) outList[i] = outList[i].Flipped();  // → B→A
        }
        else if (sb is CompoundShape cb2)
        {
            int start = outList.Count;
            cb2.CollectContacts(pb, rb, sa, pa, ra, outList);          // B-cell → A
            for (int i = start; i < outList.Count; i++) outList[i] = outList[i].SwappedParts();
        }
        else
        {
            var c = sa.Intersects(pa, ra, sb, pb, rb);
            if (c != null)
            {
                var ci = c.Value;
                if (Vector2.Dot(ci.Normal, pa - pb) < 0f) ci = ci.Flipped();
                outList.Add(ci);
            }
        }
    }

    /// <summary>
    /// Builds a Contact (precomputing effective masses + restitution bias) for a
    /// dynamic pair and wakes both bodies. Skipped if either lacks RigidBody/Velocity.
    /// </summary>
    private void GatherContact(World world, Entity a, Entity b, ContactInfo info)
    {
        if (!world.HasComponent<RigidBody>(a) || !world.HasComponent<RigidBody>(b)) return;
        if (!world.HasComponent<Velocity>(a)  || !world.HasComponent<Velocity>(b))  return;

        ref var rbA = ref world.GetComponent<RigidBody>(a);
        ref var rbB = ref world.GetComponent<RigidBody>(b);
        ref var vA  = ref world.GetComponent<Velocity>(a);
        ref var vB  = ref world.GetComponent<Velocity>(b);
        ref var tA  = ref world.GetComponent<Transform>(a);
        ref var tB  = ref world.GetComponent<Transform>(b);

        // Contact wakes both bodies.
        rbA.Asleep = false; rbA.SleepTimer = 0f;
        rbB.Asleep = false; rbB.SleepTimer = 0f;

        Vector2 n       = info.Normal;          // points B → A
        Vector2 tangent = new(-n.Y, n.X);
        Vector2 rA      = info.ContactPoint - tA.Position;
        Vector2 rB      = info.ContactPoint - tB.Position;

        float invMassA = rbA.Mass    > 0f ? 1f / rbA.Mass    : 0f;
        float invMassB = rbB.Mass    > 0f ? 1f / rbB.Mass    : 0f;
        float invIA    = rbA.Inertia > 0f ? 1f / rbA.Inertia : 0f;
        float invIB    = rbB.Inertia > 0f ? 1f / rbB.Inertia : 0f;

        float rnA = Cross(rA, n),       rnB = Cross(rB, n);
        float kn  = invMassA + invMassB + invIA * rnA * rnA + invIB * rnB * rnB;
        float rtA = Cross(rA, tangent), rtB = Cross(rB, tangent);
        float kt  = invMassA + invMassB + invIA * rtA * rtA + invIB * rtB * rtB;

        // Restitution bias from the initial approach velocity at the contact.
        Vector2 vRel = VelAt(vA, rA) - VelAt(vB, rB);
        float   vn0  = Vector2.Dot(vRel, n);
        float   e    = MathF.Min(rbA.Restitution, rbB.Restitution);
        // No bounce for already-deep overlaps (spawned-inside fragments, warp/respawn): let
        // them separate gently instead of launching apart.
        float   bias = (vn0 < -RestitutionVelThreshold && info.Depth < DeepNoBounce) ? -e * vn0 : 0f;

        _contacts.Add(new Contact
        {
            A = a, B = b,
            Normal = n, Tangent = tangent, rA = rA, rB = rB,
            InvMassA = invMassA, InvMassB = invMassB, InvIA = invIA, InvIB = invIB,
            NormalMass  = kn > 0f ? 1f / kn : 0f,
            TangentMass = kt > 0f ? 1f / kt : 0f,
            VelocityBias = bias,
            Friction = MathF.Sqrt(MathF.Max(0f, rbA.Friction) * MathF.Max(0f, rbB.Friction)),
            AccumN = 0f, AccumT = 0f,
        });
    }

    /// <summary>One sequential-impulse pass for a contact: normal impulse (clamped
    /// ≥ 0) then Coulomb friction (clamped to ±μ·normalImpulse).</summary>
    private static void SolveContact(World world, ref Contact c)
    {
        ref var vA = ref world.GetComponent<Velocity>(c.A);
        ref var vB = ref world.GetComponent<Velocity>(c.B);

        // --- Normal ---
        Vector2 vRel = VelAt(vA, c.rA) - VelAt(vB, c.rB);
        float   vn   = Vector2.Dot(vRel, c.Normal);
        float   dPn  = c.NormalMass * (c.VelocityBias - vn);

        float newN = MathF.Max(c.AccumN + dPn, 0f);
        dPn = newN - c.AccumN;
        c.AccumN = newN;

        Vector2 p = dPn * c.Normal;
        vA.Linear  += c.InvMassA * p; vA.Angular += c.InvIA * Cross(c.rA, p);
        vB.Linear  -= c.InvMassB * p; vB.Angular -= c.InvIB * Cross(c.rB, p);

        // --- Friction ---
        vRel = VelAt(vA, c.rA) - VelAt(vB, c.rB);
        float vt  = Vector2.Dot(vRel, c.Tangent);
        float dPt = c.TangentMass * (-vt);

        float maxF = c.Friction * c.AccumN;
        float newT = Math.Clamp(c.AccumT + dPt, -maxF, maxF);
        dPt = newT - c.AccumT;
        c.AccumT = newT;

        Vector2 pt = dPt * c.Tangent;
        vA.Linear  += c.InvMassA * pt; vA.Angular += c.InvIA * Cross(c.rA, pt);
        vB.Linear  -= c.InvMassB * pt; vB.Angular -= c.InvIB * Cross(c.rB, pt);
    }

    /// <summary>Deactivates bodies that have stayed below the velocity thresholds
    /// for SleepTime, zeroing their velocity. Woken by contacts/forces elsewhere.</summary>
    private static void UpdateSleep(World world, float dt)
    {
        const float linTol2 = LinSleepTol * LinSleepTol;
        world.ForEach<Velocity, RigidBody>((Entity _, ref Velocity v, ref RigidBody rb) =>
        {
            if (rb.Mass <= 0f || rb.Asleep) return;

            if (v.Linear.LengthSquared() < linTol2 && MathF.Abs(v.Angular) < AngSleepTol)
            {
                rb.SleepTimer += dt;
                if (rb.SleepTimer >= SleepTime)
                {
                    rb.Asleep  = true;
                    v.Linear   = Vector2.Zero;
                    v.Angular  = 0f;
                }
            }
            else
            {
                rb.SleepTimer = 0f;
            }
        });
    }

    // Velocity of a body at a point offset r from its centre (includes rotation).
    private static Vector2 VelAt(in Velocity v, Vector2 r) =>
        v.Linear + new Vector2(-v.Angular * r.Y, v.Angular * r.X);

    // Normal closing speed at the contact (≥0 = approaching), including each body's spin·lever.
    // Bodies without Velocity count as stationary. Normal points B→A, so approach is -vRel·n.
    private static float ApproachNormalSpeed(World world, Entity a, Entity b,
        Vector2 posA, Vector2 posB, in ContactInfo c)
    {
        Vector2 vA = world.HasComponent<Velocity>(a)
            ? VelAt(world.GetComponent<Velocity>(a), c.ContactPoint - posA) : Vector2.Zero;
        Vector2 vB = world.HasComponent<Velocity>(b)
            ? VelAt(world.GetComponent<Velocity>(b), c.ContactPoint - posB) : Vector2.Zero;
        return MathF.Max(0f, -Vector2.Dot(vA - vB, c.Normal));
    }

    // 2-D scalar cross product.
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    private struct Contact
    {
        public Entity  A, B;
        public Vector2 Normal, Tangent, rA, rB;
        public float   InvMassA, InvMassB, InvIA, InvIB;
        public float   NormalMass, TangentMass, VelocityBias, Friction;
        public float   AccumN, AccumT;   // accumulated impulses (for clamping)
    }
}
