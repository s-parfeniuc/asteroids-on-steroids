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
        Vector2 impactPoint, Vector2 impactDir, Vector2 impactorVelocity, float impactorMass,
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

        Vector2 bodyLinear = Vector2.Zero;
        float bodyAngular = 0f;
        if (world.HasComponent<Velocity>(body))
        {
            ref var v = ref world.GetComponent<Velocity>(body);
            bodyLinear = v.Linear;
            bodyAngular = v.Angular;
        }

        int cell = struckCell;
        if (cell < 0 || cell >= fb.Cells.Length)
            cell = NearestCell(fb, t.Position, t.Rotation, impactPoint);

        Vector2 dir = impactDir.LengthSquared() > 1e-8f ? Vector2.Normalize(impactDir) : Vector2.UnitX;
        float E = ComputeEnergy(impactPoint, dir, impactorVelocity, impactorMass,
                                t.Position, bodyLinear, rb.Mass, rb.Inertia, fb.Material.Restitution);
        if (E <= 0f) return;

        // If it's a piercing round, print the energy and the impactor mass, so we can tune the blast fraction to match the desired crater size.
        if (weapon.Directionality > 0.84f && weapon.BlastFraction < 0.06f)
        {
            float EDebug = ComputeEnergyDebug(impactPoint, dir, impactorVelocity, impactorMass,
                                t.Position, bodyLinear, rb.Mass, rb.Inertia, fb.Material.Restitution);
        }

        ForceLog.CurrentBody = body.Id;
        if (ForceLog.On(ForceCat.Energy, body.Id))
            ForceLog.Write(ForceCat.Energy, body.Id,
                $"E={E:0.#} (mImp={impactorMass:0.###} blast={weapon.BlastFraction:0.##} dir={weapon.Directionality:0.##}) " +
                $"vs cell0 strength≈{(fb.Bonds.Length > 0 ? fb.Bonds[0].Strength : 0f):0.#}");

        // Knockback recoil applied now, at the moment of impact; fragments inherit it via BodyLinear.
        Vector2 kick = dir * (impactorVelocity.Length() * weapon.Knockback);
        if (world.HasComponent<Velocity>(body))
        {
            ref var vKick = ref world.GetComponent<Velocity>(body);
            vKick.Linear += kick;
        }

        float effectiveDir = EffectiveDirectionality(weapon.Directionality, fb.Material.CrackDirectionality);

        // body-local impact direction
        float rc = MathF.Cos(t.Rotation), rs = MathF.Sin(t.Rotation);
        Vector2 dirLocal = new(dir.X * rc + dir.Y * rs, -dir.X * rs + dir.Y * rc);

        if (world.HasComponent<FractureProcess>(body))
        {
            ref var fp = ref world.GetComponent<FractureProcess>(body);
            var energy = new float[fb.Cells.Length];
            fp.Fronts.Add(CrackFront.Seed(energy, cell, E, dirLocal, effectiveDir,
                                          fb.Material.Brittleness, weapon.BlastFraction));
            fp.ImpactDir = dir;
            fp.ImpactPointWorld = impactPoint;
            fp.Directionality = effectiveDir;
            return;
        }

        var (spinMul, adj) = FractureSimulator.PrepareGraph(fb, bodyAngular);

        // Latent cracks: any bond already at/over strength (from prior accumulated stress) starts broken.
        var broken = new bool[fb.Bonds.Length];
        for (int i = 0; i < fb.Bonds.Length; i++)
            if (fb.Bonds[i].Stress >= fb.Bonds[i].Strength) broken[i] = true;

        var energy0 = new float[fb.Cells.Length];
        var front = CrackFront.Seed(energy0, cell, E, dirLocal, effectiveDir,
                                    fb.Material.Brittleness, weapon.BlastFraction);

        var timing = FractureTiming.FromCrackSpeed(fb.Material.CrackSpeed, FractureTiming.DefaultFixedDt);
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
            StepsPerIteration = timing.StepsPerIteration,
            FramesPerIteration = timing.FramesPerIteration,
            FrameCounter = 0,
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
        Vector2 impactPoint, Vector2 dir, Vector2 impactorVel, float impactorMass,
        Vector2 bodyPos, Vector2 bodyLinear, float mBody, float iBody, float restitution)
    {
        float vRelN = MathF.Abs(Vector2.Dot(impactorVel - bodyLinear, dir));

        Vector2 r = impactPoint - bodyPos;
        float rxn = r.X * dir.Y - r.Y * dir.X;                       // (r × n) z-component
        float invMass = (impactorMass > 0f ? 1f / impactorMass : 0f)
                      + (mBody > 0f ? 1f / mBody : 0f);
        if (iBody > 1e-6f) invMass += rxn * rxn / iBody;             // rotational lever arm
        float mEff = invMass > 1e-9f ? 1f / invMass : mBody;

        float e = Math.Clamp(restitution, 0f, 0.95f);
        // EnergyScale is the single global conversion from physical kinetic energy to fracture-energy
        // units, so REAL masses can be used everywhere (bullets, asteroid collisions) consistently.
        return FractureTuning.EnergyScale * 0.5f * mEff * vRelN * vRelN * (1f - e * e);
    }

    private static float ComputeEnergyDebug(
        Vector2 impactPoint, Vector2 dir, Vector2 impactorVel, float impactorMass,
        Vector2 bodyPos, Vector2 bodyLinear, float mBody, float iBody, float restitution)
    {
        float vRelN = MathF.Abs(Vector2.Dot(impactorVel - bodyLinear, dir));
        Console.WriteLine($"ComputeEnergy: impactorVel={impactorVel}, bodyLinear={bodyLinear}, dir={dir}, vRelN={vRelN}");
        Vector2 r = impactPoint - bodyPos;
        float rxn = r.X * dir.Y - r.Y * dir.X;                       // (r × n) z-component
        float invMass = (impactorMass > 0f ? 1f / impactorMass : 0f)
                      + (mBody > 0f ? 1f / mBody : 0f);
        if (iBody > 1e-6f) invMass += rxn * rxn / iBody;             // rotational lever arm
        float mEff = invMass > 1e-9f ? 1f / invMass : mBody;

        float e = Math.Clamp(restitution, 0f, 0.95f);
        Console.WriteLine($"E = {FractureTuning.EnergyScale * 0.5f * mEff * vRelN * vRelN * (1f - e * e)}: impactorMass={impactorMass}, mBody={mBody}, iBody={iBody}, vRelN={vRelN}, mEff={mEff}, restitution={restitution}, (1 - e * e)={1 - e * e}");
        Console.WriteLine($"{FractureTuning.EnergyScale * 0.5f * mEff * vRelN * vRelN * (1f - e * e)} (E) = {FractureTuning.EnergyScale} (EnergyScale) * 0.5 * {mEff} (mEff) * {vRelN}^2 (vRelN^2) * (1 - {e}^2 (e^2))");
        // EnergyScale is the single global conversion from physical kinetic energy to fracture-energy
        // units, so REAL masses can be used everywhere (bullets, asteroid collisions) consistently.
        return FractureTuning.EnergyScale * 0.5f * mEff * vRelN * vRelN * (1f - e * e);
    }

    private static int NearestCell(in FracturableBody fb, Vector2 pos, float rot, Vector2 worldPoint)
    {
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 d = worldPoint - pos;
        Vector2 local = new(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos);   // un-rotate into body space

        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < fb.Cells.Length; i++)
        {
            float sq = (fb.Cells[i].Centroid - local).LengthSquared();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }
}
