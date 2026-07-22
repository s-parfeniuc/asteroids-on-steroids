using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Diagnostics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// Per-shot weapon/impactor parameters. Material-intrinsic params (toughness, brittleness,
/// density, …) live on the body's <see cref="FractureProperties"/>.
/// </summary>
public struct WeaponProfile
{
    public float Directionality;   // 0 = omnidirectional splash … 1 = tight forward channel (avg'd with material)
    public float BlastFraction;    // vaporize budget carved from each cell's energy → crater size
    public float Knockback;        // one-time recoil on the struck body, as a fraction of impactor speed

    public static WeaponProfile Default => new()
    {
        Directionality = 0.4f,
        BlastFraction = 0.3f,
        Knockback = 0.01f,
    };
}

/// <summary>
/// ECS entry point for fracturing. A hit deposits dissipated energy E at the struck cell and
/// seeds a multi-frame crack front (<see cref="FractureCrackSystem"/> advances it). There is no
/// ignition gate — every hit deposits its energy and accumulates bond stress; whether anything
/// breaks is decided by bond stress vs strength downstream. The engine spawns no entities.
/// </summary>
public static class FractureService
{
    /// <summary>
    /// Seed (or extend) a crack on a body. Computes the contact-impulse dissipated energy E,
    /// applies the weapon's knockback recoil, and pushes a co-propagating front onto the body's
    /// <see cref="FractureProcess"/> (creating one if needed). Crack pacing comes from the
    /// material's CrackSpeed.
    /// </summary>
    public static void BeginFracture(
        World world, Entity body, int struckCell,
        Vector2 impactPoint, Vector2 impactDir, float normalSpeed, float impactorMass,
        in WeaponProfile weapon, Random rng)
    {
        if (!world.IsAlive(body)) return;
        if (!world.HasComponent<FracturableBody>(body) ||
            !world.HasComponent<Transform>(body) ||
            !world.HasComponent<RigidBody>(body)) return;

        ref var fb = ref world.GetComponent<FracturableBody>(body);
        ref var t = ref world.GetComponent<Transform>(body);
        ref var rb = ref world.GetComponent<RigidBody>(body);
        if (fb.Cells.Length == 0) return;

        Vector2 dir = impactDir.LengthSquared() > 1e-8f ? Vector2.Normalize(impactDir) : Vector2.UnitX;

        float E = 0f;
        if (!fb.Fragile)   // fragile bodies vaporise whole regardless of E — Seed overrides it
        {
            E = ComputeEnergy(impactPoint, dir, normalSpeed, impactorMass,
                              t.Position, rb.Mass, rb.Inertia, fb.Material.Restitution);
            if (E <= 0f) return;
        }

        // Knockback recoil applied now, at the moment of impact; fragments inherit it via BodyLinear.
        // Mass-scaled impulse: J = impactorMass·normalSpeed·Knockback (a fraction of the impactor's
        // momentum), so Δv = J / bodyMass — heavy bodies barely shift, light fragments get shoved.
        Vector2 kick = dir * (impactorMass * normalSpeed * weapon.Knockback);
        if (world.HasComponent<Velocity>(body))
        {
            ref var vKick = ref world.GetComponent<Velocity>(body);
            vKick.Linear += kick / MathF.Max(1e-3f, rb.Mass);
        }

        Seed(world, body, ref fb, ref t, struckCell, impactPoint, dir, E, impactorMass, weapon, normalSpeed);
    }

    /// <summary>
    /// Seed a crack that deposits EXACTLY <paramref name="energy"/> — no contact-impulse physics, no
    /// knockback. For callers that meter damage themselves (the piercing round pays a penetration
    /// budget per cell and deposits a tuned share of it), while the crack model stays the same.
    /// </summary>
    public static void DepositEnergy(
        World world, Entity body, int struckCell,
        Vector2 impactPoint, Vector2 impactDir, float energy, in WeaponProfile weapon,
        float normalSpeed = 0f)
    {
        if (!world.IsAlive(body)) return;
        if (!world.HasComponent<FracturableBody>(body) ||
            !world.HasComponent<Transform>(body) ||
            !world.HasComponent<RigidBody>(body)) return;

        ref var fb = ref world.GetComponent<FracturableBody>(body);
        ref var t = ref world.GetComponent<Transform>(body);
        if (fb.Cells.Length == 0) return;
        if (energy <= 0f && !fb.Fragile) return;

        Vector2 dir = impactDir.LengthSquared() > 1e-8f ? Vector2.Normalize(impactDir) : Vector2.UnitX;
        Seed(world, body, ref fb, ref t, struckCell, impactPoint, dir, energy, impactorMass: 0f, weapon, normalSpeed);
    }

    /// <summary>Common tail of both entries: resolve the struck cell, apply the fragile override,
    /// and push a co-propagating front onto the body's FractureProcess (creating one if needed).
    /// <paramref name="normalSpeed"/> sets the front's own pace (fast hits crack faster).</summary>
    private static void Seed(
        World world, Entity body, ref FracturableBody fb, ref Transform t, int struckCell,
        Vector2 impactPoint, Vector2 dir, float E, float impactorMass, in WeaponProfile weapon,
        float normalSpeed)
    {
        int cell = struckCell;
        if (cell < 0 || cell >= fb.Cells.Length)
            cell = NearestCell(fb, t.Position, t.Rotation, impactPoint);

        float effectiveDir, blast;
        if (fb.Fragile)
        {
            // Fragile crumb: one fracture vaporises it whole. Flood enough energy (omnidirectional,
            // full-blast) to pulverise every cell. The impact it inflicts on the OTHER body is
            // computed by that body's own BeginFracture, so it still deals normal damage.
            float thr = 0f;
            for (int i = 0; i < fb.Cells.Length; i++)
                thr += fb.Material.CellToughness * fb.Cells[i].Area * fb.Cells[i].DensityMult * fb.Material.Density;
            E = thr / MathF.Max(0.05f, FractureTuning.VaporEff) * 4f + 1f;
            effectiveDir = 0f;
            blast = 1f;
        }
        else
        {
            effectiveDir = EffectiveDirectionality(weapon.Directionality, fb.Material.CrackDirectionality);
            blast = weapon.BlastFraction;
        }

        ForceLog.CurrentBody = body.Id;
        if (ForceLog.On(ForceCat.Energy, body.Id))
            ForceLog.Write(ForceCat.Energy, body.Id,
                $"E={E:0.#} (mImp={impactorMass:0.###} blast={blast:0.##} dir={effectiveDir:0.##}) " +
                $"vs cell0 strength≈{(fb.Bonds.Length > 0 ? fb.Bonds[0].Strength : 0f):0.#}");

        // body-local impact direction
        float rc = MathF.Cos(t.Rotation), rs = MathF.Sin(t.Rotation);
        Vector2 dirLocal = new(dir.X * rc + dir.Y * rs, -dir.X * rs + dir.Y * rc);

        if (world.HasComponent<FractureProcess>(body))
        {
            ref var fp = ref world.GetComponent<FractureProcess>(body);

            // A Done process is one that split or finished this frame; FractureCrackSystem skips it and
            // the body is about to be replaced by its fragments. Appending a front here would swallow the
            // hit without a trace. (The one Done path whose body SURVIVES — the no-op finalise — now
            // removes its process immediately, so this branch only sees bodies on their way out.)
            if (!fp.Done)
            {
                var energy = new float[fb.Cells.Length];
                fp.Fronts.Add(CrackFront.Seed(energy, cell, E, dirLocal, effectiveDir,
                                              fb.Material.Brittleness, blast,
                                              fb.Material.CrackSpeed, normalSpeed));
                fp.ImpactDir = dir;
                fp.ImpactPointWorld = impactPoint;
                fp.Directionality = effectiveDir;
            }
            return;
        }

        float bodyAngular = 0f;
        if (world.HasComponent<Velocity>(body))
            bodyAngular = world.GetComponent<Velocity>(body).Angular;

        var (spinMul, adj) = FractureSimulator.PrepareGraph(fb, bodyAngular);

        // Latent cracks: bonds broken by a prior hit start this process already broken (permanent).
        var broken = new bool[fb.Bonds.Length];
        for (int i = 0; i < fb.Bonds.Length; i++)
            if (fb.Bonds[i].Broken) broken[i] = true;

        var energy0 = new float[fb.Cells.Length];
        var front = CrackFront.Seed(energy0, cell, E, dirLocal, effectiveDir,
                                    fb.Material.Brittleness, blast,
                                    fb.Material.CrackSpeed, normalSpeed);

        world.AddComponent(body, new FractureProcess
        {
            Fronts = new List<CrackFront> { front },
            Broken = broken,
            Pulverized = new bool[fb.Cells.Length],
            Emitted = new bool[fb.Cells.Length],
            FlingE = new float[fb.Cells.Length],
            SpinMul = spinMul,
            Adj = adj,
            ImpactDir = dir,
            ImpactPointWorld = impactPoint,
            Directionality = effectiveDir,
            Done = false,
        });
    }

    /// <summary>
    /// Combines the weapon's impact-cone focus with the material's grain/cleavage bias
    /// (arithmetic mean), so neither factor alone dominates.
    /// </summary>
    private static float EffectiveDirectionality(float weaponDir, float materialCleavage)
        => (weaponDir + materialCleavage) * 0.5f;

    /// <summary>
    /// Contact-impulse dissipated energy: effective mass (impactor point mass + the body's
    /// linear mass + its rotational lever-arm term (r×n)²/I — so spin enters the impact),
    /// then E = ½·m_eff·v_n²·(1−e²). No ignition stress — energy alone floods the graph.
    /// </summary>
    private static float ComputeEnergy(
        Vector2 impactPoint, Vector2 dir, float normalSpeed, float impactorMass,
        Vector2 bodyPos, float mBody, float iBody, float restitution)
    {
        Vector2 r = impactPoint - bodyPos;
        float rxn = r.X * dir.Y - r.Y * dir.X;                       // (r × n) z-component
        float invMass = (impactorMass > 0f ? 1f / impactorMass : 0f)
                      + (mBody > 0f ? 1f / mBody : 0f);
        if (iBody > 1e-6f) invMass += rxn * rxn / iBody;             // rotational lever arm
        float mEff = invMass > 1e-9f ? 1f / invMass : mBody;

        float e = Math.Clamp(restitution, 0f, 0.95f);
        // normalSpeed is the true pre-solve normal closing speed (supplied by the caller — no velocity
        // reconstruction). EnergyScale converts physical KE to fracture-energy units (real masses).
        return FractureTuning.EnergyScale * 0.5f * mEff * normalSpeed * normalSpeed * (1f - e * e);
    }

    /// <summary>
    /// The cell an impact point lands on, in the body's local frame. Prefers the cell that
    /// CONTAINS the point; failing that, the cell nearest by distance to its polygon — never by
    /// centroid, which for a big or elongated surface cell happily returns an interior neighbour
    /// and seeds the crack inside the body while the struck face stays intact.
    /// </summary>
    private static int NearestCell(in FracturableBody fb, Vector2 pos, float rot, Vector2 worldPoint)
    {
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 d = worldPoint - pos;
        Vector2 local = new(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos);   // un-rotate into body space

        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            var poly = fb.Cells[i].Local;
            if (ContainsPoint(poly, local)) return i;
            float dist = DistanceToPolygon(poly, local);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    private static bool ContainsPoint(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    private static float DistanceToPolygon(Vector2[] poly, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            Vector2 a = poly[j], b = poly[i];
            Vector2 ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 1e-9f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
            float dist = (p - (a + ab * t)).Length();
            if (dist < best) best = dist;
        }
        return best;
    }
}
