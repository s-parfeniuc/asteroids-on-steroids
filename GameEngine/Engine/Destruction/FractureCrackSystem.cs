using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Diagnostics;
using AsteroidsEngine.Engine.Events;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>A cell vaporised mid-propagation — the game turns this into dust and polygon
/// debris. Carries the cell's world-space polygon and its inherited surface motion so the
/// game can cut it into collider-less debris pieces that fly apart and fade.</summary>
public readonly struct CellPulverizedEvent
{
    public readonly Entity Body;
    public readonly Vector2 WorldCentroid;
    public readonly float Area;
    public readonly Vector2[] WorldVerts;   // the cell polygon, world space
    public readonly Vector2 CellVelocity;   // inherited velocity at the cell centroid (linear + ω×r)
    public readonly float BodyAngular;      // parent spin (debris inherit a share)
    public CellPulverizedEvent(Entity body, Vector2 worldCentroid, float area,
                               Vector2[] worldVerts, Vector2 cellVelocity, float bodyAngular)
    {
        Body = body; WorldCentroid = worldCentroid; Area = area;
        WorldVerts = worldVerts; CellVelocity = cellVelocity; BodyAngular = bodyAngular;
    }
}

/// <summary>A multi-frame fracture finished; the body should be replaced by these
/// fragments (pulverised cells were already vaporised live, so they're not included).</summary>
public readonly struct FractureCompletedEvent
{
    public readonly Entity Body;
    public readonly FragmentSpec[] Fragments;
    public FractureCompletedEvent(Entity body, FragmentSpec[] fragments)
    { Body = body; Fragments = fragments; }
}

/// <summary>A cracking body split mid-spread into pieces. The game spawns each piece and,
/// where a piece is still cracking (LivePiece.Process set), attaches that process so it
/// keeps propagating. The original body is then destroyed.</summary>
public readonly struct FractureSplitEvent
{
    public readonly Entity Body;
    public readonly List<LivePiece> Pieces;
    public FractureSplitEvent(Entity body, List<LivePiece> pieces)
    { Body = body; Pieces = pieces; }
}

/// <summary>
/// Advances every live <see cref="FractureProcess"/>: each iteration steps its fronts a
/// few frontier-pops (co-propagating through the shared broken-bond state), vaporises
/// cells whose crack energy crossed the blast threshold (→ CellPulverizedEvent), and when
/// all fronts are spent finalises the body into fragments (→ FractureCompletedEvent). The
/// game wires the events (dust VFX, spawning fragments, destroying the original).
/// </summary>
public sealed class FractureCrackSystem : ISystem
{
    private readonly EventBus _bus;
    private readonly Random _rng;
    private readonly List<Entity> _scratch = new();

    public FractureCrackSystem(EventBus bus, Random rng) { _bus = bus; _rng = rng; }

    public void Update(World world, double dt)
    {
        _scratch.Clear();
        _scratch.AddRange(world.QueryEntities<FractureProcess>());

        foreach (var e in _scratch)
        {
            if (!world.IsAlive(e) || !world.HasComponent<FractureProcess>(e)) continue;
            ref var fp = ref world.GetComponent<FractureProcess>(e);
            if (fp.Done) continue;

            if (++fp.FrameCounter < fp.FramesPerIteration) continue;
            fp.FrameCounter = 0;

            ref var body = ref world.GetComponent<FracturableBody>(e);

            // --- advance the fronts, co-propagating through the shared Broken[] ---
            int steps = fp.StepsPerIteration < 1 ? 1 : fp.StepsPerIteration;
            for (int s = 0; s < steps; s++)
            {
                bool any = false;
                foreach (var f in fp.Fronts)
                {
                    if (!f.Active) continue;
                    FractureKernel.StepFront(f, body.Cells, body.Bonds, fp.Adj, fp.Eff, fp.Broken);
                    any = true;
                }
                if (!any) break;
            }

            // --- vaporise cells whose crack energy crossed the blast threshold ---
            ref var t = ref world.GetComponent<Transform>(e);
            float cos = MathF.Cos(t.Rotation), sin = MathF.Sin(t.Rotation);
            Vector2 bodyLin = Vector2.Zero; float bodyAng = 0f;
            if (world.HasComponent<Velocity>(e))
            {
                ref var bv = ref world.GetComponent<Velocity>(e);
                bodyLin = bv.Linear; bodyAng = bv.Angular;
            }
            foreach (var f in fp.Fronts)
            {
                float[] en = f.Energy;
                for (int i = 0; i < body.Cells.Length; i++)
                {
                    if (fp.Pulverized[i] || en[i] <= 0f || en[i] <= f.BlastThresh) continue;
                    fp.Pulverized[i] = true;

                    var srcLocal = body.Cells[i].Local;
                    var wv = new Vector2[srcLocal.Length];
                    for (int v = 0; v < srcLocal.Length; v++)
                        wv[v] = new(srcLocal[v].X * cos - srcLocal[v].Y * sin + t.Position.X,
                                    srcLocal[v].X * sin + srcLocal[v].Y * cos + t.Position.Y);

                    Vector2 lc = body.Cells[i].Centroid;
                    Vector2 wc = new(lc.X * cos - lc.Y * sin + t.Position.X,
                                     lc.X * sin + lc.Y * cos + t.Position.Y);
                    Vector2 r = wc - t.Position;
                    Vector2 cellVel = bodyLin + new Vector2(-bodyAng * r.Y, bodyAng * r.X);
                    _bus.Publish(new CellPulverizedEvent(e, wc, body.Cells[i].Area, wv, cellVel, bodyAng));
                }
            }

            ForceLog.CurrentBody = e.Id;   // fragment-construction logs attribute to this body

            // --- split: if the body has broken into ≥2 pieces and detachment is on,
            //     partition it into fresh pieces now (the continuer keeps cracking) ---
            if (fp.DetachOnSplit &&
                FractureSimulator.CountComponents(body.Cells.Length, body.Bonds, fp.Broken, fp.Pulverized) >= 2)
            {
                var pieces = FractureSimulator.SplitLive(body, BuildInput(world, e, in fp, in t), in fp, _rng);
                fp.Done = true;
                _bus.Publish(new FractureSplitEvent(e, pieces));
                continue;
            }

            // --- finalise once every front is spent (single remaining piece, or the
            //     finalise-at-end fallback when detachment is off) ---
            bool active = false;
            foreach (var f in fp.Fronts) if (f.Active) { active = true; break; }
            if (active) continue;

            // In detach mode the body has already shed its chunks; the single remaining
            // piece keeps its motion (no fresh fling). Finalise-at-end mode bursts.
            var fragments = FractureSimulator.BuildResult(body, BuildInput(world, e, in fp, in t),
                                                          fp.Broken, fp.Pulverized, _rng,
                                                          fling: !fp.DetachOnSplit);
            fp.Done = true;
            _bus.Publish(new FractureCompletedEvent(e, fragments));
        }
    }

    private static FractureInput BuildInput(World world, Entity e, in FractureProcess fp, in Transform t)
    {
        Vector2 lin = Vector2.Zero; float ang = 0f;
        if (world.HasComponent<Velocity>(e))
        {
            ref var v = ref world.GetComponent<Velocity>(e);
            lin = v.Linear; ang = v.Angular;
        }
        float mass = world.HasComponent<RigidBody>(e) ? world.GetComponent<RigidBody>(e).Mass : 1f;

        return new FractureInput
        {
            ImpactPointWorld = fp.ImpactPointWorld,
            ImpactDir = fp.ImpactDir,
            MomentumKick = fp.MomentumKick,
            EjectSpeed = fp.EjectSpeed,
            ImpactSpin = fp.ImpactSpin,
            Directionality = fp.Directionality,
            BodyPosition = t.Position,
            BodyRotation = t.Rotation,
            BodyLinear = lin,
            BodyAngular = ang,
            BodyMass = mass,
        };
    }
}
