using System;
using AsteroidsSim.Math;

namespace AsteroidsSim.Fracture;

/// <summary>
/// A material, authored by <b>failure strain</b> rather than by a multiplied real strength.
/// </summary>
/// <remarks>
/// <para><b>Why strain and not stress.</b> Stretchiness is strength divided by stiffness, so
/// inflating strength to make bodies survive game-speed impacts — the prototype's earlier
/// <c>sigma_real x 300</c> — inflates stretchiness by the same factor. That produced rock at 4%
/// failure strain and steel at 44%: a 30 px steel cell stretched 13 px before it even yielded and
/// needed a 158 px gap to separate. A single multiplier provably cannot put rock and steel in one
/// strain band because their real strains differ by 11x. Authoring strain directly fixes the
/// rigidity, and strength follows as <c>sigma = rho c^2 eps</c>.</para>
///
/// <para><b>The three knobs are separable, and that is the whole calibration story.</b> Fragment
/// count goes as <c>M^2 / (eps^2 chi)</c> while the rigid look depends on <c>eps</c> alone, so a
/// fast impact is stopped from vaporising a body by raising <see cref="Chi"/>, not by slowing the
/// game down. Measured on the two-asteroid collision at 900 px/s, sweeping chi moved mass in
/// &gt;4-cell pieces across 75/46/0/83% while peak neck stayed at 11.1% of a cell throughout.</para>
///
/// <list type="bullet">
/// <item><see cref="Strain"/> — rigidity of the bulk (peak elastic stretch under load).</item>
/// <item><see cref="Duct"/> — how far material necks before it tears. This bounds visible stretch,
/// and it is why raising <see cref="Chi"/> does not make anything stretchier.</item>
/// <item><see cref="Chi"/> — energy per unit of new surface, i.e. how much a given impact
/// fragments. A bond separates at <c>chi * eps * cellSize</c>, so <c>chi * eps</c> is the
/// crack-opening gap as a fraction of a cell; keeping it under ~0.4 keeps every gap inside half a
/// cell.</item>
/// </list>
/// </remarks>
public readonly struct Material
{
    public readonly string Name;
    /// <summary>Density in kg/m³.</summary>
    public readonly float Rho;
    /// <summary>Wave speed in m/s.</summary>
    public readonly float C;
    /// <summary>Failure strain (dimensionless).</summary>
    public readonly float Strain;
    /// <summary>Cohesive softening ratio s_f / s_0.</summary>
    public readonly float Chi;
    /// <summary>Fraction of the peak stretch at which plastic flow begins.</summary>
    public readonly float Yield;
    /// <summary>Plastic strain capacity before the bond tears, as a fraction of cell size.</summary>
    public readonly float Duct;

    public Material(string name, float rho, float c, float strain, float chi, float yield, float duct)
    {
        Name = name; Rho = rho; C = c; Strain = strain; Chi = chi; Yield = yield; Duct = duct;
    }

    /// <summary>The prototype's table (<c>MATERIALS</c>), verbatim.</summary>
    public static readonly Material Rock = new("rock", 3000f, 5000f, 0.010f, 90f, 0.95f, 0.02f);
    public static readonly Material Ice = new("ice", 917f, 3200f, 0.007f, 70f, 0.70f, 0.05f);
    public static readonly Material Glass = new("glass", 2500f, 5500f, 0.008f, 1.05f, 9f, 0.00f);
    public static readonly Material Sandstone = new("sandstone", 2200f, 2500f, 0.009f, 50f, 0.85f, 0.05f);
    public static readonly Material Steel = new("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f);

    public static Material ByName(string name) => name switch
    {
        "ice" => Ice,
        "glass" => Glass,
        "sandstone" => Sandstone,
        "steel" => Steel,
        _ => Rock,
    };
}

/// <summary>
/// A material resolved against the world scale and the two global trim knobs — what actually gets
/// baked into a body. Mirrors the prototype's <c>matProps()</c>.
/// </summary>
public readonly struct MaterialProps
{
    /// <summary>Density in the simulation's own units (g/cm³), as the prototype carries it.</summary>
    public readonly float Rho;
    /// <summary>Wave speed in px/s.</summary>
    public readonly float Cpx;
    /// <summary>Failure strain after the trim.</summary>
    public readonly float Eps;
    /// <summary>Critical particle velocity in px/s: strength follows strain.</summary>
    public readonly float VCrit;
    public readonly float Chi;
    public readonly float Yield;
    public readonly float Duct;

    public MaterialProps(in Material m, in SimTuning t)
    {
        Rho = m.Rho / 1000f;
        Cpx = m.C * t.PxPerMetre;
        Eps = m.Strain * t.StrainScale;
        VCrit = Eps * Cpx;
        Chi = 1f + (m.Chi - 1f) * t.ToughnessScale;
        Yield = m.Yield;
        Duct = m.Duct;
    }
}

/// <summary>
/// Every global knob the solver reads. A value type carried inside the simulation rather than a
/// static, because static mutable state inside the tick is a determinism hazard and cannot be
/// snapshotted (PORT_PLAN §6 rule 5).
/// </summary>
public struct SimTuning
{
    public float PxPerMetre;
    public float StrainScale;      // trim on every material's failure strain — the rigidity knob
    public float ToughnessScale;   // trim on (chi - 1) — the fragmentation knob
    public float YieldScale;
    public float FlowRate;         // viscoplastic flow rate, 1/s
    public float ShearMul;         // shear strength as a fraction of tensile
    public float Relax;            // Rayleigh damping on the deviation field = wave attenuation
    public float ContactMu;
    public float ContactCMin;      // contact stiffness floor, m/s
    public float RateSens;
    public float RateRef;
    public int Substeps;
    public bool Centrifugal;
    public bool Euler;
    public bool Coriolis;
    public bool Dust;

    // structure authoring layer — writes BondStr at build time, never read by the solver
    public float WeibullM;
    public float Aniso;
    public float GrainAngle;
    public bool GrainLock;
    public float SurfFlaw;

    /// <summary>The prototype's defaults (<c>cfg</c>).</summary>
    public static SimTuning Default => new()
    {
        PxPerMetre = 0.52f,
        StrainScale = 1f,
        ToughnessScale = 1f,
        YieldScale = 1f,
        FlowRate = 25f,
        ShearMul = 0.6f,
        Relax = 2f,
        ContactMu = 0.6f,
        ContactCMin = 5000f,
        RateSens = 0f,
        RateRef = 1f,
        Substeps = 9,
        Centrifugal = true,
        Euler = true,
        Coriolis = true,
        Dust = true,
        WeibullM = 8f,
        Aniso = 0f,
        GrainAngle = 0f,
        GrainLock = false,
        SurfFlaw = 0f,
    };

    /// <summary>
    /// Substeps the CFL condition demands for a given wave speed and cell size:
    /// <c>sub &gt;= margin * c / (60 * cellSize)</c>. With mixed materials this must be taken over
    /// the fastest material present, so it is a world-level figure even though cell size is not.
    /// </summary>
    public static int CflSubsteps(float cPx, float cellSize, float margin = 2f)
        => (int)SimMath.Ceiling(margin * cPx / (60f * cellSize));
}
