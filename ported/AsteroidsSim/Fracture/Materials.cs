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
    /// Energy needed to destroy one unit of area — the cost of carving.
    /// </summary>
    /// <remarks>
    /// Conceptually what <see cref="Chi"/> already is for bonds: energy per unit of new surface.
    /// This is what couples destruction to momentum transfer, because the energy has to come from
    /// the contact doing work, and the contact can only do work if the impactor decelerates.
    /// </remarks>
    public readonly float CrushRate;

    /// <summary>
    /// How much of its ORIGINAL area a cell may LOSE before it comminutes — not how much it may
    /// shrink to. Glass may shed 15% and then shatters; steel endures 50% of erosion first.
    /// </summary>
    public readonly float ShedLimit;

    /// <summary>How wide a dent is, in MEAN POLYGON EDGES of the loaded cell: the kernel radius
    /// over which one contact's recession is spread across neighbouring surface vertices.</summary>
    /// <remarks>
    /// <para>The one parameter carving v2 adds. Narrow gives pits (glass), wide gives dents (steel).
    /// A multiple of a cell length rather than pixels, so it means the same thing at any grain.</para>
    /// <para>The base is the cell's mean edge, not its radius — see <c>Solver.MeanEdge</c> for why,
    /// and note that these values were re-derived when the base changed. On the old radius base the
    /// kernel came out about twice the cell, so every corner of a loaded cell drew near-equal weight
    /// and a pointy cell contracted toward its centroid instead of flattening where it was struck.
    /// Values here are half the old ones, which puts the Penetrator at the measured optimum for a
    /// rod tip while preserving every material's width relative to the others. A normal collide is
    /// insensitive to this over 0.40-2.00 (bodies 3-8, area kept 75-79%, median cell aspect
    /// 1.21-1.23): the parameter only bites where a sharp feature meets a surface.</para>
    /// </remarks>
    public readonly float Dent;



    public Material(string name, float rho, float c, float strain, float chi, float yield, float duct,
        float crush, float crushEnergy, float shedLimit, float dent = 0.75f)
    {
        Name = name; Rho = rho; C = c; Strain = strain; Chi = chi; Yield = yield; Duct = duct;
        Crush = crush; CrushRate = crushEnergy; ShedLimit = shedLimit; Dent = dent;
    }

    // ── The comminution pair ─────────────────────────────────────────────────────
    //
    // Crushing has the same shape as bond damage — one number for where it STARTS, one for how much
    // it takes to FINISH — and the two are independent axes, exactly as s0 and chi are:
    //
    // CrushRate is a DIMENSIONLESS multiplier on the impedance-derived yield speed, so it must not
    // re-encode softness: rho*c already does that, and doing it twice is what vaporised every soft
    // material in the collide scene. Only genuine deviations from the impedance expectation belong
    // here — glass powders faster than its stiffness suggests, steel resists beyond its own.
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
    //                                              rho     c    strain  chi   yield duct   crush  rate  shed  dent
    public static readonly Material Rock = new("rock", 3000f, 5000f, 0.010f, 90f, 0.95f, 0.02f, 2.5e5f, 1.0f, 0.35f, 0.75f);
    public static readonly Material Ice = new("ice", 917f, 3200f, 0.007f, 70f, 0.70f, 0.05f, 6.0e4f, 1.0f, 0.25f, 0.75f);
    public static readonly Material Glass = new("glass", 2500f, 5500f, 0.008f, 30f, 9f, 0.00f, 4.0e5f, 2.0f, 0.15f, 0.40f);
    public static readonly Material Sandstone = new("sandstone", 2200f, 2500f, 0.009f, 50f, 0.85f, 0.05f, 1.5e5f, 1.0f, 0.25f, 0.75f);
    public static readonly Material Steel = new("steel", 7850f, 5900f, 0.020f, 50f, 0.35f, 0.50f, 1.0e6f, 0.3f, 0.50f, 1.25f);

    /// <summary>A long-rod penetrator: dense, and authored to resist its OWN erosion.</summary>
    /// <remarks>
    /// The distinguishing property is not mass but that it does not mushroom. A bullet erodes as it
    /// goes — its front cells reach ShedLimit and comminute, the rod shortens and spreads, and it
    /// ends up delivering its momentum over a wide crater. This one is given a crush threshold three
    /// times steel's and a third of its erosion rate, so it stays a rod and keeps driving into the
    /// hole it has already made. Tungsten's density, and a high shed limit so that even when it does
    /// start eroding it survives a long way.
    /// </remarks>
    public static readonly Material Penetrator = new("penetrator", 17000f, 4000f, 0.015f, 40f, 0.40f, 0.30f, 3.0e6f, 0.1f, 0.60f, 1.00f);

    public static Material ByName(string name) => name switch
    {
        "ice" => Ice,
        "glass" => Glass,
        "sandstone" => Sandstone,
        "steel" => Steel,
        _ => Rock,
    };

    /// <summary>Exact field equality — what material-table registration dedupes on.</summary>
    public bool SameAs(in Material o)
        => Name == o.Name && Rho == o.Rho && C == o.C && Strain == o.Strain && Chi == o.Chi
           && Yield == o.Yield && Duct == o.Duct && Crush == o.Crush
           && CrushRate == o.CrushRate && ShedLimit == o.ShedLimit && Dent == o.Dent;
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
    /// <summary>Energy per unit area destroyed.</summary>
    public readonly float CrushRate;
    /// <summary>Fraction of original area a cell may shed before comminuting.</summary>
    public readonly float ShedLimit;
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
        CrushRate = m.CrushRate;
        ShedLimit = m.ShedLimit;
    }
}

/// <summary>
/// How a single body is put together: the heterogeneity that decides WHERE it cracks, as opposed to
/// the material, which decides how hard that is.
/// </summary>
/// <remarks>
/// <para>Per body rather than per material, because two asteroids of the same rock can be one a
/// uniform lump and the other visibly bedded, and because this is the hook a shape-authoring tool
/// writes into. All of it is applied once, at build, by <c>BodyBuilder.ApplyStructure</c>, and it
/// scales bond STRENGTH only — never stiffness, which would make the wave field heterogeneous for
/// no reason. (A genuine material boundary is a different matter; see per-cell materials.)</para>
///
/// <para><see cref="From"/> returns the scene-wide defaults out of <see cref="SimTuning"/>, so a
/// caller that does not care about structure gets exactly today's behaviour.</para>
/// </remarks>
public readonly struct BodyStructure
{
    /// <summary>Weibull modulus for per-bond strength scatter. 0 disables it; low = wide spread.</summary>
    public readonly float WeibullM;
    /// <summary>Bedding-plane anisotropy: bonds along the grain are stronger, across it weaker.</summary>
    public readonly float Aniso;
    /// <summary>Grain direction in radians, used only when <see cref="GrainLock"/> is set.</summary>
    public readonly float GrainAngle;
    /// <summary>Fix the grain instead of drawing it per body.</summary>
    public readonly bool GrainLock;
    /// <summary>How much weaker a bond is when its cells sit on the boundary. Real brittle solids
    /// crack from surface defects, and this is what lets glass initiate at all.</summary>
    public readonly float SurfFlaw;

    public BodyStructure(float weibullM, float aniso, float grainAngle, bool grainLock, float surfFlaw)
    {
        WeibullM = weibullM; Aniso = aniso; GrainAngle = grainAngle;
        GrainLock = grainLock; SurfFlaw = surfFlaw;
    }

    /// <summary>The scene-wide defaults — what every body used before structure became per body.</summary>
    public static BodyStructure From(in SimTuning t)
        => new(t.WeibullM, t.Aniso, t.GrainAngle, t.GrainLock, t.SurfFlaw);
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
    /// The hard ceiling on penetration, as a fraction of the thinner cell's half-extent along the
    /// contact normal. Past it, carving is forced whatever the pressure reads. 0 disables it.
    /// </summary>
    /// <remarks>
    /// <para>Bodies never spawn overlapping, so an overlap is never just solver slack: it is the
    /// contact telling us that velocity correction and pressure-driven carving between them did not
    /// keep the two cells apart. Past this depth one of them must give way, and the one that gives
    /// is the one whose material yields first — the forced recession is split in inverse proportion
    /// to the two crush thresholds, so rock yields to a penetrator and like yields to like evenly.
    /// It runs through the ordinary carve path, so ShedMass still hands the lost mass and its
    /// momentum to the partner and the ledger stays exact; a cell driven deep enough is eroded to
    /// nothing through the same path, which is its comminution.</para>
    /// <para>It is a BACKSTOP, not the mechanism. With <see cref="CrushConfine"/> at 1.0 the depth
    /// term alone held the worst penetration to 0.16-0.27 cell radii across the reference scenes,
    /// so at 0.3 this is there to guarantee the bound, not to produce it.</para>
    /// <para>The yardstick is the half-extent along the normal rather than <c>CellRad</c>, because
    /// the circumradius measures a cell's longest reach, not how much material lies in the
    /// direction it is being pushed: on the piercing round's tip cell it is 53.7 px against a
    /// half-width of 7.9, so a ceiling on it would let a partner pass clean through the rod
    /// sideways. For a round cell the two agree.</para>
    /// </remarks>
    public float OverlapBackstop;

    /// <summary>
    /// How strongly an eroded surface is pulled to meet its neighbour's at the side they share.
    /// </summary>
    /// <remarks>
    /// <para>Cells clip independently, so two neighbours under similar load can recede by different
    /// amounts and leave a STEP where their shared side reaches the outside. The cure is not a
    /// shared depth or a shared direction — sharing the direction would only make the steps
    /// parallel, which reads worse than ragged. What has to match is the POINT at which each cell's
    /// eroded surface crosses their shared side.</para>
    ///
    /// <para>That point is already known: the touch record's interval endpoint is exactly where the
    /// neighbour's erosion cut the shared side. So each cell keeps its own direction and its own
    /// depth, and only its plane OFFSET is relaxed toward passing through that point. 0 leaves the
    /// steps exactly as they are; 1 makes the surfaces meet. Creases — two surfaces meeting at an
    /// angle — are left alone, because that is faceting rather than an artifact.</para>
    /// </remarks>
    public float CarveContinuity;

    /// <summary>Smallest clip worth making, as a fraction of the cell's current area.</summary>
    /// <remarks>
    /// A floor on the size of a single cut, not on the rate of erosion: demand below it accumulates
    /// in <see cref="SimState.CellCarvePend"/> rather than being discarded, so the total area shed
    /// over time is unchanged and only the granularity of the geometry changes.
    /// </remarks>
    public float CarveMinArea;

    /// <summary>A crack transmits compression: broken bonds between live cells keep their
    /// compressive normal force, and none of their tension or shear.</summary>
    /// <remarks>
    /// Without it, from the substep a bond breaks until the body is split at the end of the tick,
    /// the two sides of a crack have no interaction of any kind — same-body cells never contact —
    /// so a detached front row slides into the row behind it with no resistance. A clump of cells
    /// that are still touching behaves like a bonded body in compression; this is what lets it.
    /// </remarks>
    public bool CrackPush;

    /// <summary>How far a crack may be squeezed shut, as a multiple of the bond's failure stretch,
    /// before its compressive force stops growing.</summary>
    /// <remarks>
    /// <para><b>Not optional.</b> An unbroken bond is bounded by its own damage law: push it far
    /// enough and it breaks. A broken one under <see cref="CrackPush"/> can only push, so two cells
    /// driven together accumulate compression with nothing to stop them — same-body pairs are
    /// rejected by the narrow phase, so there is no contact to resolve the interpenetration either.
    /// Shipped uncapped it diverged: on a mass-24 rod at grain 170 the deepest crack reached
    /// 6.05e11 px and bodies reached 2.6e18 px/s within one tick, and the resulting 1027 NaNs
    /// scattered cells across the scene until every pair was a broadphase candidate — 338,448 SAT
    /// calls a tick against 174 once bounded.</para>
    ///
    /// <para>A multiple of the failure stretch, because that is the scale at which the interface
    /// stops behaving like the material: beyond it the faces are in hard contact, which is the
    /// contact solver's business and not a bond's.</para>
    /// </remarks>
    public float CrackPushCap;

    /// <summary>A body splits along a crack only once the crack has OPENED — while the broken bond's
    /// faces are still pressed together the two sides remain one body.</summary>
    /// <remarks>
    /// A piece still pressed against the body is mechanically coupled to it: contact impulses on it
    /// belong to the whole body, not to a fragment. Splitting the tick a bond breaks handed the
    /// impact to a one-row fragment and the bulk behind it never decelerated (glass collide: body 0
    /// at 300 → 279 px/s while it lost half its mass). Requires <see cref="CrackPush"/>, which is
    /// what tracks the closing across a broken bond.
    /// </remarks>
    public bool SplitOnOpen;



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
        CrushConfine = 1.0f,
        OverlapBackstop = 0.3f,
        CarveContinuity = 0.5f,
        CarveMinArea = 0.005f,
        CrackPush = true,
        CrackPushCap = 4f,
        SplitOnOpen = false,
        SpallFraction = 1f,
        ExportFreeDebris = false,
        ContactBias = 0.05f,
        ContactMaxBias = 2f,
        ContactCMin = 5000f,
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
