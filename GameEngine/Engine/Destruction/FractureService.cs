using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Diagnostics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// Per-shot weapon/projectile parameters — the impactor's contribution to a fracture.
/// Different bullet types carry different WeaponProfiles. Material-intrinsic params
/// (toughness, brittleness, SurfaceEfficiency, SpinPreStress, …) live on the body's
/// FractureProperties instead. (docs/destruction_engine_spec.md §4.)
/// </summary>
public struct WeaponProfile
{
    public float Directionality;       // 0 = omnidirectional blast … 1 = tight forward channel; combined with material's CrackDirectionality at impact
    public float MomentumTransfer;     // forward push along the shot, as a fraction of bullet speed
    public float EjectFraction;        // radial scatter, as a fraction of bullet speed
    public float ImpactSpin;           // rad/s scale for impact-induced shear spin on fragments
    public float BlastFraction;        // vaporisation crater: 0 = none (pure fragmentation) … 1 = total
    public float EnergyScale;          // multiplier on the raw kinetic impact energy (1 = physical;
                                       // <1 = only a fraction couples into fracture, e.g. for
                                       // asteroid-on-asteroid where raw KE dwarfs the crack budget)

    public static WeaponProfile Default => new()
    {
        Directionality   = 0.4f,
        MomentumTransfer = 0.01f,
        EjectFraction    = 0.08f,
        ImpactSpin       = 0.5f,
        BlastFraction    = 0.3f,
        EnergyScale      = 1f,
    };
}

/// <summary>
/// ECS entry point for fracturing (the thin-engine boundary, spec §5). Given a hit
/// on a FracturableBody entity it reads the body's components, computes the impact
/// energy (reduced-mass + spin pre-stress), runs the FractureSimulator, and:
///   • sub-threshold → accumulates the energy on the body in place, returns false;
///   • at/above threshold → returns true with a FractureResult.
/// The engine spawns no entities — the caller wires the result (spawn fragments,
/// destroy the original) and may raise its own gameplay events.
/// </summary>
public static class FractureService
{
    public static bool TryFracture(
        World world, Entity body, int struckCell,
        Vector2 impactPoint, Vector2 impactDir, Vector2 impactorVelocity, float impactorMass,
        Random rng, out FractureResult result)
        => TryFracture(world, body, struckCell, impactPoint, impactDir, impactorVelocity,
                       impactorMass, WeaponProfile.Default, rng, out result);

    public static bool TryFracture(
        World world, Entity body, int struckCell,
        Vector2 impactPoint, Vector2 impactDir, Vector2 impactorVelocity, float impactorMass,
        in WeaponProfile weapon, Random rng, out FractureResult result)
    {
        result = default;

        if (!world.IsAlive(body)) return false;
        if (!world.HasComponent<FracturableBody>(body) ||
            !world.HasComponent<Transform>(body) ||
            !world.HasComponent<RigidBody>(body)) return false;

        ref var fb = ref world.GetComponent<FracturableBody>(body);
        ref var t = ref world.GetComponent<Transform>(body);
        ref var rb = ref world.GetComponent<RigidBody>(body);
        if (fb.Cells.Length == 0) return false;

        Vector2 bodyLinear = Vector2.Zero;
        float bodyAngular = 0f;
        if (world.HasComponent<Velocity>(body))
        {
            ref var v = ref world.GetComponent<Velocity>(body);
            bodyLinear = v.Linear;
            bodyAngular = v.Angular;
        }

        // Resolve the struck cell (raycast PartIndex; fall back to nearest).
        int cell = struckCell;
        if (cell < 0 || cell >= fb.Cells.Length)
            cell = NearestCell(fb, t.Position, t.Rotation, impactPoint);

        // --- Energy model ---
        Vector2 dir = impactDir.LengthSquared() > 1e-8f ? Vector2.Normalize(impactDir) : Vector2.UnitX;
        float mBody = rb.Mass;
        float vRelN = MathF.Abs(Vector2.Dot(impactorVelocity - bodyLinear, dir));
        float mRed = (impactorMass + mBody) > 0f ? impactorMass * mBody / (impactorMass + mBody) : impactorMass;
        // Impact energy only. Spin does NOT add energy (rotational KE of a heavy body
        // dwarfs a bullet by orders of magnitude); spin acts as a bounded bond
        // pre-stress in the simulator instead.
        float eImpact = 0.5f * mRed * vRelN * vRelN;

        // Fragment dynamics are scaled to bullet SPEED (intuitive + visible), not to
        // physical momentum (which is negligible for a light bullet vs a heavy body).
        float bulletSpeed = impactorVelocity.Length();
        Vector2 kick = dir * (bulletSpeed * weapon.MomentumTransfer);
        float ejectSpeed = bulletSpeed * weapon.EjectFraction;

        var input = new FractureInput
        {
            StruckCell = cell,
            EnergyTotal = eImpact,
            ImpactPointWorld = impactPoint,
            ImpactDir = dir,
            Directionality = EffectiveDirectionality(weapon.Directionality, fb.Material.CrackDirectionality),
            SpinOmega = bodyAngular,
            MomentumKick = kick,
            EjectSpeed = ejectSpeed,
            ImpactSpin = weapon.ImpactSpin,
            BlastFraction = weapon.BlastFraction,
            BodyPosition = t.Position,
            BodyRotation = t.Rotation,
            BodyLinear = bodyLinear,
            BodyAngular = bodyAngular,
            BodyMass = mBody,
        };

        result = FractureSimulator.Simulate(fb, input, rng);

        if (!result.Fractured)
        {
            fb.State.AbsorbedEnergy = result.AbsorbedEnergy;   // accumulate sub-threshold damage in place
            return false;
        }
        return true;
    }

    /// <summary>
    /// Multi-frame counterpart of <see cref="TryFracture"/>. Computes the same impact
    /// energy, then — instead of fracturing atomically — seeds a crack front that
    /// <see cref="FractureCrackSystem"/> advances over the next frames. A hit on a body
    /// that is already cracking pushes another co-propagating front onto its process.
    /// Sub-threshold hits accumulate energy in place (no front), as in the atomic path.
    /// Results arrive asynchronously via CellPulverizedEvent / FractureCompletedEvent.
    /// </summary>
    public static void BeginFracture(
        World world, Entity body, int struckCell,
        Vector2 impactPoint, Vector2 impactDir, Vector2 impactorVelocity, float impactorMass,
        in WeaponProfile weapon, in FractureTiming timing, Random rng)
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

        // Same energy model as TryFracture (reduced-mass impact energy).
        Vector2 dir = impactDir.LengthSquared() > 1e-8f ? Vector2.Normalize(impactDir) : Vector2.UnitX;
        float mBody = rb.Mass;
        float vRelN = MathF.Abs(Vector2.Dot(impactorVelocity - bodyLinear, dir));
        float mRed = (impactorMass + mBody) > 0f ? impactorMass * mBody / (impactorMass + mBody) : impactorMass;
        float escale = weapon.EnergyScale > 0f ? weapon.EnergyScale : 1f;
        float eImpact = 0.5f * mRed * vRelN * vRelN * escale;

        ForceLog.CurrentBody = body.Id;   // engine fragment code logs against the source body
        if (ForceLog.On(ForceCat.Energy, body.Id))
            ForceLog.Write(ForceCat.Energy, body.Id,
                $"eImpact = ½·mRed{mRed:0.#}·vRelN{vRelN:0.#}²·scale{escale:0.#####} = {eImpact:0.#}  " +
                $"(mRed = mImp{impactorMass:0.##}·mBody{mBody:0.#}/(sum))");

        var budget = FractureSimulator.ComputeBudget(fb, cell, mBody, eImpact);
        if (ForceLog.On(ForceCat.Energy, body.Id))
            ForceLog.Write(ForceCat.Energy, body.Id,
                budget.Fractured
                    ? $"budget: surface {budget.Surface:0.#} + kinetic {budget.Kinetic:0.#} (threshold passed)"
                    : $"budget: SUB-THRESHOLD, absorbed {budget.Absorbed:0.#} (no fracture)");
        if (!budget.Fractured)
        {
            fb.State.AbsorbedEnergy = budget.Absorbed;   // sub-threshold: accumulate, no front
            return;
        }

        float bulletSpeed = impactorVelocity.Length();
        Vector2 kick = dir * (bulletSpeed * weapon.MomentumTransfer);
        float ejectSpeed = bulletSpeed * weapon.EjectFraction;

        // Recoil now, at the moment of impact — the body reacts immediately instead of
        // waiting for the fracture to finish. Fragments inherit this via BodyLinear.
        if (world.HasComponent<Velocity>(body))
        {
            ref var vKick = ref world.GetComponent<Velocity>(body);
            if (ForceLog.On(ForceCat.Recoil, body.Id))
                ForceLog.Write(ForceCat.Recoil, body.Id,
                    $"kick = dir{ForceLog.V(dir)}·(speed{bulletSpeed:0.#}·MT{weapon.MomentumTransfer:0.###}) = {ForceLog.V(kick)}  " +
                    $"v.Linear {ForceLog.V(vKick.Linear)}→{ForceLog.V(vKick.Linear + kick)}");
            vKick.Linear += kick;
        }

        // Crack front for this hit: struck cell holds the full impact energy, spends the
        // surface budget; blast threshold matches the atomic path (1-blast)·E.
        float rc = MathF.Cos(t.Rotation), rs = MathF.Sin(t.Rotation);
        Vector2 dirLocal = new(dir.X * rc + dir.Y * rs, -dir.X * rs + dir.Y * rc);
        float blast = Math.Clamp(weapon.BlastFraction, 0f, 1f);
        float blastThresh = blast > 0f ? (1f - blast) * eImpact : float.PositiveInfinity;
        float transmission = FractureSimulator.TransmissionFor(fb.Material.Brittleness);
        float effectiveDir = EffectiveDirectionality(weapon.Directionality, fb.Material.CrackDirectionality);

        var energy = new float[fb.Cells.Length];
        var front = CrackFront.Seed(energy, cell, eImpact, budget.Surface,
                                    dirLocal, effectiveDir, transmission, blastThresh);

        if (world.HasComponent<FractureProcess>(body))
        {
            // Co-propagate: add the front, refresh the fling snapshot to the latest hit.
            ref var fp = ref world.GetComponent<FractureProcess>(body);
            fp.Fronts.Add(front);
            fp.ImpactDir = dir;
            fp.ImpactPointWorld = impactPoint;
            fp.MomentumKick = kick;
            fp.EjectSpeed = ejectSpeed;
            fp.ImpactSpin = weapon.ImpactSpin;
            fp.Directionality = effectiveDir;
            return;
        }

        var (eff, adj) = FractureSimulator.PrepareGraph(fb, bodyAngular);
        world.AddComponent(body, new FractureProcess
        {
            Fronts = new List<CrackFront> { front },
            Broken = new bool[fb.Bonds.Length],
            Pulverized = new bool[fb.Cells.Length],
            Eff = eff,
            Adj = adj,
            ImpactDir = dir,
            ImpactPointWorld = impactPoint,
            MomentumKick = kick,
            EjectSpeed = ejectSpeed,
            ImpactSpin = weapon.ImpactSpin,
            Directionality = effectiveDir,
            StepsPerIteration = timing.StepsPerIteration < 1 ? 1 : timing.StepsPerIteration,
            FramesPerIteration = timing.FramesPerIteration < 1 ? 1 : timing.FramesPerIteration,
            FrameCounter = 0,
            DetachOnSplit = timing.DetachOnSplit,
            Done = false,
        });
    }

    /// <summary>
    /// Combines the weapon's impact-cone focus with the material's grain/cleavage bias.
    /// Arithmetic mean: neither factor alone dominates — a focused shot through isotropic
    /// metal is moderated, an omnidirectional grenade in brittle glass still cleaves somewhat.
    /// </summary>
    private static float EffectiveDirectionality(float weaponDir, float materialCleavage)
        => (weaponDir + materialCleavage) * 0.5f;

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
