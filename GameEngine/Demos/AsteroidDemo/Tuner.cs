// Live-tunable parameter set, grouped by ownership (WEAPON / MATERIAL / PHYSICS / SPAWN).
// Up/Down selects a parameter, Left/Right adjusts it. [R] = takes effect on next spawn.

using AsteroidsEngine.Engine.Components;

sealed class Param
{
    public readonly string Name;
    public float Value;
    public readonly float Min, Max, Step;
    public readonly bool IsHeader;

    public Param(string name, float value, float min, float max, float step)
    {
        Name = name; Value = value; Min = min; Max = max; Step = step;
    }

    private Param(string header) { Name = header; IsHeader = true; }
    public static Param Header(string name) => new(name);

    public void Adjust(int dir) => Value = Math.Clamp(Value + dir * Step, Min, Max);

    public string Display =>
        Step >= 1f    ? Value.ToString("0")
      : Step >= 0.01f ? Value.ToString("0.00")
      :                 Value.ToString("0.000");
}

sealed class Tuner
{
    public readonly List<Param> Params = new();
    public int Selected;

    public Param Add(string name, float value, float min, float max, float step)
    {
        var p = new Param(name, value, min, max, step);
        Params.Add(p);
        return p;
    }

    public void Header(string name) => Params.Add(Param.Header(name));

    public void Move(int d)
    {
        if (Params.Count == 0) return;
        int n = Params.Count;
        do { Selected = (Selected + d + n) % n; } while (Params[Selected].IsHeader);
    }

    public void Adjust(int d)
    {
        if (Params.Count > 0 && !Params[Selected].IsHeader) Params[Selected].Adjust(d);
    }

    public void SelectFirst()
    {
        for (int i = 0; i < Params.Count; i++)
            if (!Params[i].IsHeader) { Selected = i; return; }
    }
}

/// <summary>All tunable parameters, grouped by ownership. WEAPON params form the
/// per-shot WeaponProfile; MATERIAL params are the per-body FractureProperties.</summary>
sealed class Config
{
    public readonly Tuner T = new();

    // WEAPON — per-shot WeaponProfile
    public readonly Param BulletSpeed, BulletMass, FireRate, EnergyScale,
                          Directionality, MomentumTransfer, EjectFraction, ImpactSpin, Blast;
    // MATERIAL — per-body FractureProperties
    public readonly Param Brittleness, Toughness, SurfaceEff, SpinPreStress, KineticFraction, MinFragArea, Grain, Density;
    // PHYSICS — live, all bodies
    public readonly Param Restitution, Friction, LinDrag, AngDrag;
    // PLAYER — direct velocity control (independent of physics drag)
    public readonly Param Thrust, MaxSpeed, Impulse, Brake;
    // VFX — presentation; auto-modulated by impact energy / cell area / material
    public readonly Param DustCount, DustSize, DustTtl, DustSpeed, DustSpread,
                          FlashSize, FlashTtl, TracerLen, TracerWidth;
    // FRACTURE — multi-frame crack pacing. CrackSteps is material-owned (preset fills it);
    // CrackFrames is a global override. Speed = steps / (frames · fixedDt) pops/sec.
    // Detach: 1 = chunks fall off as cracks reach them, 0 = whole body splits at the end.
    public readonly Param CrackSteps, CrackFrames, Detach, DetachScale, DetachJitter;
    // Asteroid-on-asteroid collision fracture: scale & blast are separate from bullet params.
    // AstDirSpin blends crack direction from the contact normal (0, head-on) to the full
    // relative velocity incl. spin (1).
    public readonly Param AsteroidEnergyScale, AsteroidBlast, AstDirSpin;
    // DEBRIS — polygon chunks shed by vaporised cells (collider-less, fade over TTL).
    public readonly Param DebrisTtl, DebrisScatter;
    // VORTEX — live, applied every frame to all physics bodies outside the deadzone.
    public readonly Param VortexDeadzone, VortexCentripetal, VortexTangential, VortexCapFrames;
    // SPAWN
    public readonly Param AstCount, AstRadius, AstSpeed, AstSpin;

    public Config()
    {
        T.Header("-- WEAPON --");
        BulletSpeed      = T.Add("Bullet speed",     900f, 100f, 4000f, 50f);
        BulletMass       = T.Add("Bullet mass",      0.20f, 0.02f, 5f, 0.02f);
        FireRate         = T.Add("Fire cooldown",    0.12f, 0.02f, 1f, 0.02f);
        EnergyScale      = T.Add("Energy x",         1.0f, 0.1f, 30f, 0.1f);
        Directionality   = T.Add("Directionality",   0.40f, 0f, 1f, 0.05f);
        MomentumTransfer = T.Add("Bullet push",      0.01f, 0f, 1f, 0.01f);
        EjectFraction    = T.Add("Eject speed",      0.08f, 0f, 0.5f, 0.01f);
        ImpactSpin       = T.Add("Impact spin",      0.5f, 0f, 10f, 0.1f);
        Blast            = T.Add("Blast (vaporise)", 0.30f, 0f, 1f, 0.05f);

        T.Header("-- MATERIAL --");
        Brittleness      = T.Add("Brittleness",      0.60f, 0f, 1f, 0.05f);
        Toughness        = T.Add("Toughness",        16f, 1f, 300f, 1f);
        SurfaceEff       = T.Add("Surface eff.",     0.12f, 0.01f, 1f, 0.01f);
        SpinPreStress    = T.Add("Spin pre-stress",  0.12f, 0f, 2f, 0.02f);
        KineticFraction  = T.Add("Kinetic frac",     0.35f, 0f, 1f, 0.05f);
        MinFragArea      = T.Add("Min frag area",    180f, 20f, 2000f, 20f);
        Grain            = T.Add("Grain [R]",        1400f, 300f, 6000f, 100f);
        Density          = T.Add("Density [R]",      1.4f, 0.2f, 5f, 0.1f);

        T.Header("-- PHYSICS --");
        Restitution      = T.Add("Restitution",      0.30f, 0f, 1f, 0.05f);
        Friction         = T.Add("Friction",         0.20f, 0f, 1f, 0.05f);
        LinDrag          = T.Add("Linear drag",      0.05f, 0f, 2f, 0.05f);
        AngDrag          = T.Add("Angular drag",     0.05f, 0f, 2f, 0.05f);

        T.Header("-- PLAYER --");
        // Arcade movement: instant impulse on key press + sustained accel + manual brake.
        Impulse          = T.Add("Impulse",          130f,  0f,  400f, 10f);  // px/s burst on press
        Thrust           = T.Add("Accel rate",       2200f, 0f, 6000f, 100f); // px/s² while held
        MaxSpeed         = T.Add("Max speed",        650f,  100f,1800f, 25f); // px/s cap
        Brake            = T.Add("Brake (s⁻¹)",      6.0f,  0f,  20f,  0.5f);// decel when released

        T.Header("-- VFX --");
        DustCount        = T.Add("Dust count",       14f, 0f, 60f, 1f);
        DustSize         = T.Add("Dust size",        2.6f, 0.5f, 10f, 0.2f);
        DustTtl          = T.Add("Dust ttl",         0.70f, 0.1f, 3f, 0.1f);
        DustSpeed        = T.Add("Dust speed",       60f, 0f, 400f, 10f);
        DustSpread       = T.Add("Dust spread",      0.50f, 0f, 1f, 0.05f);
        FlashSize        = T.Add("Flash size",       22f, 0f, 120f, 2f);
        FlashTtl         = T.Add("Flash ttl",        0.12f, 0.02f, 0.6f, 0.02f);
        TracerLen        = T.Add("Tracer length",    26f, 0f, 120f, 2f);
        TracerWidth      = T.Add("Tracer width",     2f, 0.5f, 8f, 0.5f);

        T.Header("-- FRACTURE --");
        CrackSteps       = T.Add("Crack steps/it",   2f, 1f, 30f, 1f);
        CrackFrames      = T.Add("Frames/iter",      1f, 1f, 20f, 1f);
        Detach           = T.Add("Detach split",     1f, 0f, 1f, 1f);
        DetachScale      = T.Add("Detach scale",     0.90f, 0.50f, 1f, 0.01f);
        DetachJitter     = T.Add("Detach jitter",    0.02f, 0f, 0.10f, 0.01f);
        AsteroidEnergyScale = T.Add("Ast E scale",  0.0002f, 0.00001f, 0.01f, 0.00005f);
        AsteroidBlast    = T.Add("Ast blast",        0.08f, 0f, 0.5f, 0.01f);
        AstDirSpin       = T.Add("Ast dir spin",     1.0f, 0f, 1f, 0.05f);

        T.Header("-- DEBRIS --");
        DebrisTtl        = T.Add("Debris ttl",       0.80f, 0.1f, 3f, 0.1f);
        DebrisScatter    = T.Add("Debris scatter",   40f, 0f, 200f, 5f);

        T.Header("-- VORTEX --");
        VortexDeadzone    = T.Add("Deadzone",        900f,  100f, 2800f, 50f);
        VortexCentripetal = T.Add("Centripetal k",   0.10f, 0f,   0.50f, 0.01f);
        VortexTangential  = T.Add("Tangential k",    0.06f, 0f,   0.30f, 0.01f);
        VortexCapFrames   = T.Add("Cap frames",      100f,  10f,  500f,  10f);

        T.Header("-- SPAWN [R] --");
        AstCount         = T.Add("Asteroids",        6f, 1f, 40f, 1f);
        AstRadius        = T.Add("Radius",           110f, 40f, 250f, 10f);
        AstSpeed         = T.Add("Speed",            45f, 0f, 300f, 5f);
        AstSpin          = T.Add("Spin",             0.6f, 0f, 5f, 0.1f);

        T.SelectFirst();
    }

    // ---- Material presets (cycle with M, then they fill the MATERIAL sliders) ----
    private static readonly (string Name, FractureProperties Mat)[] Presets =
    {
        ("Rock",  FractureProperties.Rock),
        ("Glass", FractureProperties.Glass),
        ("Ice",   FractureProperties.Ice),
        ("Metal", FractureProperties.Metal),
    };
    private int _matIndex;
    public string MaterialName => Presets[_matIndex].Name;

    public void CycleMaterial()
    {
        _matIndex = (_matIndex + 1) % Presets.Length;
        var m = Presets[_matIndex].Mat;
        Brittleness.Value     = m.Brittleness;
        Toughness.Value       = m.Toughness;
        SurfaceEff.Value      = m.SurfaceEfficiency;
        SpinPreStress.Value   = m.SpinPreStress;
        KineticFraction.Value = m.KineticFraction;
        MinFragArea.Value     = m.MinFragmentArea;
        Grain.Value           = m.GrainArea;
        Density.Value         = m.Density;
        CrackSteps.Value      = m.CrackSpeed;
        DetachScale.Value     = m.DetachCellScale;
        DetachJitter.Value    = m.DetachCellJitter;
    }
}
