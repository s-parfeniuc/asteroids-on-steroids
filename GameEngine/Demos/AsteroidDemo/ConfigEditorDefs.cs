using AsteroidsEngine.Engine.Ui;
using AsteroidsGame.Config;

namespace AsteroidDemo;

/// <summary>
/// Pre-built editor bindings for each GameConfig type.
/// Each EditorDef is built once at startup (expression trees compile to delegates).
/// Usage:  if (ConfigEditorDefs.Material.Draw(mat, ui)) SaveConfig();
/// </summary>
static class ConfigEditorDefs
{
    // ── Material ──────────────────────────────────────────────────────────────
    public static readonly EditorDef<MaterialConfig> Material =
        new EditorDef<MaterialConfig>()
            .Slider(m => m.Toughness,            "Toughness",         0.5f,   40f)
            .Slider(m => m.Brittleness,          "Brittleness",       0f,     1f)
            .Slider(m => m.CrackDirectionality,  "Crack Direction.",  0f,     1f)
            .Slider(m => m.GrainArea,            "Grain Area",        200f,   6000f, "0")
            .Slider(m => m.MinFragmentArea,      "Min Fragment",      20f,    1000f, "0")
            .Separator()
            .Slider(m => m.Density,              "Density",           0.2f,   6f)
            .Slider(m => m.KineticFraction,      "Kinetic Frac.",     0f,     1f)
            .Slider(m => m.SurfaceEfficiency,    "Surface Eff.",      0f,     0.5f)
            .Slider(m => m.SpinPreStress,        "Spin PreStress",    0f,     0.5f)
            .Separator()
            .Slider(m => m.CrackSpeed,           "Crack Speed",       0.1f,   6f)
            .Slider(m => m.DetachCellScale,      "Detach Scale",      0.5f,   1.1f)
            .Slider(m => m.DetachCellJitter,     "Detach Jitter",     0f,     0.2f);

    // ── Weapon (base fields shared by all weapon types) ───────────────────────
    public static readonly EditorDef<WeaponConfig> Weapon =
        new EditorDef<WeaponConfig>()
            .Slider(w => w.FireRate,          "Fire Rate",        0.5f,   15f)
            .Slider(w => w.ProjectileSpeed,   "Proj. Speed",      100f,   1500f, "0")
            .Slider(w => w.TimeToLive,        "Time to Live",     0.5f,   6f)
            .Separator()
            .Slider(w => w.Energy,            "Energy",           0f,     600f,  "0")
            .Slider(w => w.Directionality,    "Directionality",   0f,     1f)
            .Slider(w => w.MomentumTransfer,  "Momentum Xfer",    0f,     0.05f)
            .Slider(w => w.EjectFraction,     "Eject Frac.",      0f,     0.5f)
            .Slider(w => w.ImpactSpin,        "Impact Spin",      0f,     2f)
            .Slider(w => w.BlastFraction,     "Blast Frac.",      0f,     1f);

    // ── Procedural asteroid shape ─────────────────────────────────────────────
    public static readonly EditorDef<ProceduralAsteroidConfig> ProceduralShape =
        new EditorDef<ProceduralAsteroidConfig>()
            .Slider(p => p.BaseRadius,         "Base Radius",      20f,    200f, "0")
            .Slider(p => p.Roughness,          "Roughness",        0f,     0.6f)
            .Slider(p => p.NoiseFrequency,     "Noise Freq.",      0.5f,   8f)
            .Slider(p => p.ConcavityBias,      "Concavity Bias",   0f,     0.4f)
            .Slider(p => p.SeedClusterCenter,  "Cluster Center",   0f,     1f)
            .Separator()
            // Arrays need explicit lambdas; expression trees can't assign array elements.
            .SliderInt("Verts Min", p => p.VertexCount[0], (p, v) => p.VertexCount[0] = v, 4, 24)
            .SliderInt("Verts Max", p => p.VertexCount[1], (p, v) => p.VertexCount[1] = v, 4, 32)
            ;

    // ── Skill (base fields shared by all skill types) ─────────────────────────
    public static readonly EditorDef<SkillConfig> Skill =
        new EditorDef<SkillConfig>()
            .Slider(s => s.Cooldown, "Cooldown",  0.5f, 30f)
            .Slider(s => s.Duration, "Duration",  0.1f, 10f);

    // ── Player ────────────────────────────────────────────────────────────────
    public static readonly EditorDef<PlayerConfig> Player =
        new EditorDef<PlayerConfig>()
            .Slider(p => p.ShapeScale, "Shape Scale",  0.1f,  2f)
            .Separator()
            .Slider(p => p.Impulse,     "Impulse",       0f,    500f,  "0")
            .Slider(p => p.Thrust,      "Accel Rate",    0f,  8000f,  "0")
            .Slider(p => p.MaxSpeed,    "Max Speed",   100f,  2000f,  "0")
            .Slider(p => p.BrakeDrag,   "Brake Rate",    0f,    20f)
            .Slider(p => p.LateralDrag, "Lateral Drag",  0f,    20f)
            .Slider(p => p.RotSpeed,    "Rot Speed",    0.5f,    8f);
}
