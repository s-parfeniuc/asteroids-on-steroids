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

    /// <summary>
    /// Contact stress at which comminution begins, in the solver's 2D stress units (force per unit
    /// contact length). Authored, not derived — see the comminution pair below.
    /// </summary>
    public readonly float Crush;

    /// <summary>
    /// Stress-seconds above <see cref="Crush"/> a cell absorbs before it is powder.
    /// </summary>
    public readonly float CrushCap;

    public Material(string name, float rho, float c, float strain, float chi, float yield, float duct,
        float crush, float crushCap)
    {
        Name = name; Rho = rho; C = c; Strain = strain; Chi = chi; Yield = yield; Duct = duct;
        Crush = crush; CrushCap = crushCap;
    }

    // ── The comminution pair ─────────────────────────────────────────────────────
    //
    // Crushing has the same shape as bond damage — one number for where it STARTS, one for how much
    // it takes to FINISH — and the two are independent axes, exactly as s0 and chi are:
    //
    //   Crush     the pressure a cell must feel before any comminution happens at all
    //   CrushCap  how much it then absorbs before it is powder — the brittle/ductile axis
    //
    // CAPACITY IS A TIME CONSTANT, and that is the thing to understand before touching it. A stress
    // wave needs a few ticks to cross a body — about four for a 190 px rock at 2600 px/s. If the
    // cells at the contact powder faster than that, the impact is absorbed at the surface, the
    // interior is never loaded, and the body does not fracture at all however violent the hit. It
    // is not subtle: on the 900 px/s projectile, capacity 6e3 gave 0 broken bonds and 1 body, while
    // 2e5 gave 17 broken and 8 bodies with the same threshold. Comminution and fracture compete for
    // the same impact, and capacity is what arbitrates.
    //
    // The threshold then separates the regimes, and it works because dose rate is (press - Crush).
    // Under an impact press is far above the threshold, so the rate hardly notices where the
    // threshold sits; under a sustained squeeze press is only just above it, so the rate is almost
    // entirely a function of the threshold. Lowering it therefore buys slow-crush behaviour at
    // almost no cost to fracture — which is what lets ONE pair of numbers serve a slow press and a
    // hypervelocity impact, the thing the previous design could never do.
    //
    // Both are authored per material rather than derived. The previous version computed the
    // threshold as a fixed fraction of the tensile failure stress rho*c^2*eps, which cannot be
    // right for more than one material at a time: the compressive-to-tensile ratio is about 15x for
    // rock, 10x for glass and near 1x for steel, so no single fraction spans them. A global floor
    // was then needed to rescue the soft end, which is the usual sign that a derivation is being
    // patched rather than fixed.
    //
    // Glass is the shape the pair exists to express: a HIGH threshold — it is genuinely strong in
    // compression, stronger than rock — with a NEARLY ZERO capacity, so once it does start it goes
    // to powder almost as soon as it starts. Steel is the opposite corner, high in both. Their
    // capacities differ by 100x, and the previous design could not express any of it: capacity was
    // one global constant shared by every material in the scene.
    //
    // CALIBRATED AGAINST BEHAVIOUR in four regimes at once — `FractureBench --crush` and
    // `--crushcap` print the sweeps these came from. Every pair must: lose NO cells in a field left
    // in light contact and never driven (the erosion the deleted floor was patching); comminute
    // under a sustained slow squeeze; and leave a projectile impact still fracturing its target.
    // Steel is the deliberate exception to the middle one — it does not powder under a slow press,
    // because bending and denting are what should answer that for steel once they exist.
    //
    // Read against tensile these run high and unevenly, and that is expected rather than a fault:
    // the contact stiffness floor means a soft material's contacts are as stiff as a hard one's, so
    // the pressure a weak material actually experiences does not scale down with its own strength.
    // No derived fraction of tensile strength can absorb that, which is why these are authored.
    //
    // Ordering, hardest last:  threshold  ice < sandstone < rock < glass < steel
    //                          capacity   glass < ice < sandstone < rock < steel  (brittle first)

    /// <summary>The prototype's table (<c>MATERIALS</c>), plus the authored comminution pair.</summary>
    //                                                  rho     c     strain  chi   yield duct   crush   cap
    public static readonly Material Rock = new("rock", 3000f, 5000f, 0.010f, 90f, 0.95f, 0.02f, 2.5e5f, 2.0e5f);
    public static readonly Material Ice = new("ice", 917f, 3200f, 0.007f, 70f, 0.70f, 0.05f, 6.0e4f, 5.0e4f);
    public static readonly Material Glass = new("glass", 2500f, 5500f, 0.008f, 1.05f, 9f, 0.00f, 4.0e5f, 2.0e4f);
    public static readonly Material Sandstone = new("sandstone", 2200f, 2500f, 0.009f, 50f, 0.85f, 0.05f, 1.5e5f, 1.0e5f);
    public static readonly Material Steel = new("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 1.0e6f, 2.0e6f);

    public static Material ByName(string name) => name switch
    {
        "ice" => Ice,
        "glass" => Glass,
        "sandstone" => Sandstone,
        "steel" => Steel,
        _ => Rock,
    };

    // ── the id table ─────────────────────────────────────────────────────────
    //
    // One byte per cell indexes this, which is what lets a single body hold cells of different
    // materials without giving every cell its own copy of eight floats. The table is a few hundred
    // bytes and permanently cache-resident, so a lookup through it costs nothing measurable.
    //
    // IDS ARE PART OF THE CONTENT CONTRACT. They are written into cells and therefore into the
    // fingerprint and every snapshot, so reordering this array renumbers material identity in a
    // saved or networked simulation. Append only.

    private static readonly Material[] Table = { Rock, Ice, Glass, Sandstone, Steel };

    /// <summary>The material for a cell's stored id.</summary>
    public static ref readonly Material ById(byte id)
        => ref Table[id < Table.Length ? id : 0];

    /// <summary>The id to store on a cell built from this material. O(table), build-time only.</summary>
    public static byte IdOf(in Material m)
    {
        for (int i = 0; i < Table.Length; i++)
            if (ReferenceEquals(Table[i].Name, m.Name) || Table[i].Name == m.Name) return (byte)i;
        return 0;
    }
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
    /// <summary>Contact stress at which this material begins to comminute.</summary>
    public readonly float CrushStress;
    /// <summary>Stress-seconds above the threshold this material absorbs before it is powder.</summary>
    public readonly float CrushCap;
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

        // Authored, and deliberately NOT scaled by the trim knobs. StrainScale and ToughnessScale
        // trim how a material stretches and how it fragments in TENSION; comminution is a
        // compressive failure and is not the same axis, so a scene tuned to be more or less brittle
        // in tension does not silently move the pressure at which cells turn to powder.
        CrushStress = m.Crush;
        CrushCap = m.CrushCap;
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

    /// <summary>Multiplier on contact compliance. >1 softens the contact and deepens overlap.</summary>
    public float ContactCompliance;

    /// <summary>
    /// How much of the contact modulus a unit of penetration strain contributes to crush pressure.
    /// </summary>
    /// <remarks>
    /// <para>The pressure a cell feels has two parts, and the impulse alone measures only one of
    /// them. <c>Ln</c> is what the solve had to spend to stop material approaching — it measures
    /// BRAKING. A cell wedged between two others that have already stopped moving is being squeezed
    /// just as hard and spends nothing, so on the impulse term alone a static press reads as almost
    /// no load at all, which is exactly what the measurements showed: peak stress barely moved
    /// between a 1 px and a 64 px overlap.</para>
    ///
    /// <para>This term supplies the missing half. Penetration over contact length is a compressive
    /// strain, and the contact modulus <c>rho*c^2</c> turns it into a stress on the same scale as
    /// the impulse term, so the two add. It is deliberately <b>uncapped</b>, unlike the positional
    /// bias in the solve: the cap there exists so a deep first-substep overlap does not fire
    /// material apart, and lifting it would change the contact response. Here there is nothing to
    /// destabilise — depth is only being read — and capping it is precisely what made confinement
    /// invisible.</para>
    /// </remarks>
    public float CrushConfine;

    /// <summary>
    /// Fraction of a snapping bond's stored elastic energy that becomes fly-apart kinetic energy.
    /// </summary>
    /// <remarks>
    /// <para>The rest is taken to go where it goes in a real solid: into the new crack surfaces, and
    /// into elastic waves radiated back into the bulk that disperse rather than throwing anything.
    /// 1.0 — the original behaviour — puts ALL of it into fragment velocity, which is the
    /// unphysical extreme rather than the neutral choice, and it reads on screen as fragments
    /// leaving far too fast.</para>
    ///
    /// <para><b>Safe to lower, structurally.</b> ReleaseRecoil applies its impulse equal and
    /// opposite, so momentum is conserved for ANY value of this — the symmetry is what conserves it,
    /// not the magnitude. And a smaller value injects less energy, so the "never above 100%" energy
    /// guardrail can only get safer. What changes is morphology alone.</para>
    ///
    /// <para>Ejection speed goes as the square root: with the cells momentarily at rest relative to
    /// each other the impulse is sqrt(2 en mu), so 0.25 halves the speed and 0.0625 quarters it.
    /// </para>
    /// </remarks>
    public float SpallFraction;

    /// <summary>Export debris that has touched nothing for a while, rather than leaving it in the world.</summary>
    public bool ExportFreeDebris;

    /// <summary>
    /// Fraction of the remaining penetration a contact removes per substep. With deformation gone
    /// this is the only mechanism resolving overlap, so it is load-bearing rather than a nicety.
    /// </summary>
    public float ContactBias;

    /// <summary>Penetration beyond which the bias stops growing, in px.</summary>
    public float ContactMaxBias;
    public float ContactCMin;      // contact stiffness floor, m/s
    public float RateSens;
    public float RateRef;
    public int Substeps;

    /// <summary>
    /// How far a body may move, as a fraction of the smallest cell, before the contact manifold is
    /// re-derived instead of refreshed. Pure performance/accuracy trade: 0 rebuilds every substep
    /// (the old behaviour), large values never rebuild inside a tick.
    ///
    /// <para>0.02 is where measurement put it, judged against ensembles rather than single runs.
    /// Peak penetration is the metric that responds systematically rather than chaotically, and it
    /// is the one that reads as a feel change: at 0.02 the projectile scene penetrates 8.9-10.4 px
    /// against a reference of 8.8-11.2, while at 0.05 it reaches 21 px and at 0.25 nearly 30 — two
    /// cells sharing most of their area. Steel shows the same step, 16-18 px up to 0.10 and 22-26
    /// beyond. The saving is nearly all collected by 0.02 anyway: SAT calls fall 3x to 11x
    /// depending on the scene, and a dense pile costs only 17% more here than at 0.10.</para>
    ///
    /// <para>The threshold counts ONE body's motion, so two bodies converging reach it at twice the
    /// rate; the effective relative bound is 0.04 of a cell. It is also a global maximum, which is
    /// this design's weak point: one fast body forces every contact in the scene to be re-derived.
    /// Making staleness per-body is the next refinement, and it shares its machinery with the
    /// sleeping work.</para>
    /// </summary>
    public float ManifoldDrift;

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
        ContactCompliance = 1f,
        CrushConfine = 0.05f,
        SpallFraction = 1f,
        ExportFreeDebris = false,
        ContactBias = 0.2f,
        ContactMaxBias = 2f,
        ContactCMin = 5000f,
        RateSens = 0f,
        RateRef = 1f,
        ManifoldDrift = 0.02f,
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
