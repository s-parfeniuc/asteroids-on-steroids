// Live-tunable parameter set. Up/Down selects a parameter, Left/Right adjusts it.
// Parameters tagged [R] only take effect on the next asteroid spawn (press R).

sealed class Param
{
    public readonly string Name;
    public float Value;
    public readonly float Min, Max, Step;

    public Param(string name, float value, float min, float max, float step)
    {
        Name = name; Value = value; Min = min; Max = max; Step = step;
    }

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

    public void Move(int d)   { if (Params.Count > 0) Selected = (Selected + d + Params.Count) % Params.Count; }
    public void Adjust(int d) { if (Params.Count > 0) Params[Selected].Adjust(d); }
}

/// <summary>All tunable physics constants. Live ones are read at use; [R] ones are
/// read when spawning asteroids.</summary>
sealed class Config
{
    public readonly Tuner T = new();

    // Weapon (live)
    public readonly Param BulletSpeed, BulletMass, FireRate, EnergyScale, Directionality;
    // Energy model (live)
    public readonly Param SpinEnergyFrac, MomentumTransfer;
    // Material (live — applied to all bodies each frame)
    public readonly Param Brittleness, KineticFraction, MinFragArea;
    // Body physics (live — applied to all bodies each frame)
    public readonly Param Restitution, Friction, LinDrag, AngDrag, Thrust;
    // Spawn-time ([R] to apply)
    public readonly Param AstCount, AstRadius, AstSpeed, AstSpin, Grain, Toughness, Density;

    public Config()
    {
        BulletSpeed      = T.Add("Bullet speed",     900f, 100f, 4000f, 50f);
        BulletMass       = T.Add("Bullet mass",      0.20f, 0.02f, 5f, 0.02f);
        FireRate         = T.Add("Fire cooldown",    0.12f, 0.02f, 1f, 0.02f);
        EnergyScale      = T.Add("Energy x",         1.0f, 0.1f, 30f, 0.1f);
        Directionality   = T.Add("Directionality",   0.40f, 0f, 1f, 0.05f);
        SpinEnergyFrac   = T.Add("Spin energy frac", 0.35f, 0f, 2f, 0.05f);
        MomentumTransfer = T.Add("Momentum xfer",    0.55f, 0f, 2f, 0.05f);
        Brittleness      = T.Add("Brittleness",      0.60f, 0f, 1f, 0.05f);
        KineticFraction  = T.Add("Kinetic frac",     0.35f, 0f, 1f, 0.05f);
        MinFragArea      = T.Add("Min frag area",    180f, 20f, 2000f, 20f);
        Restitution      = T.Add("Restitution",      0.30f, 0f, 1f, 0.05f);
        Friction         = T.Add("Friction",         0.20f, 0f, 1f, 0.05f);
        LinDrag          = T.Add("Linear drag",      0.05f, 0f, 2f, 0.05f);
        AngDrag          = T.Add("Angular drag",     0.05f, 0f, 2f, 0.05f);
        Thrust           = T.Add("Thrust",           900f, 0f, 6000f, 50f);
        AstCount         = T.Add("Asteroids [R]",    6f, 1f, 40f, 1f);
        AstRadius        = T.Add("Radius [R]",       110f, 40f, 250f, 10f);
        AstSpeed         = T.Add("Speed [R]",        45f, 0f, 300f, 5f);
        AstSpin          = T.Add("Spin [R]",         0.6f, 0f, 5f, 0.1f);
        Grain            = T.Add("Grain [R]",        1400f, 300f, 6000f, 100f);
        Toughness        = T.Add("Toughness [R]",    16f, 1f, 200f, 1f);
        Density          = T.Add("Density [R]",      1.4f, 0.2f, 5f, 0.1f);
    }
}
