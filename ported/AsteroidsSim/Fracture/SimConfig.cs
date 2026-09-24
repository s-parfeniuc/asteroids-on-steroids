using System;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Everything the simulation is configured by: the global tuning (which carries the model
/// constants) and the material table.
/// </summary>
/// <remarks>
/// The simulation never reads a file. Hosts load this from <c>Assets/sim.json</c> through
/// <c>AsteroidsSim.Config.SimConfigFile</c> and hand it in, so that file is the single source of
/// truth for every number the model is built from.
/// </remarks>
public sealed class SimConfig
{
    public SimTuning Tuning;
    public Material[] Materials = Array.Empty<Material>();

    /// <summary>The material named <paramref name="name"/>; throws if the table has none.</summary>
    public Material Material(string name)
    {
        foreach (var m in Materials)
            if (m.Name == name) return m;
        throw new ArgumentException($"no material named '{name}' in the configuration", nameof(name));
    }

    /// <summary>A copy that can be modified without touching this one.</summary>
    public SimConfig Clone() => new() { Tuning = Tuning, Materials = (Material[])Materials.Clone() };
}

/// <summary>
/// The constants the model is built from that are not tuning knobs: scales, thresholds and the
/// geometric tolerances every matching rule uses. Carried inside <see cref="SimTuning"/> so the
/// solver and the builder read them from the same value they read everything else from.
/// </summary>
/// <remarks>
/// Build-time constants are <c>double</c> because body construction runs in double; runtime ones
/// are <c>float</c>. Division guards (1e-6 and smaller) and exact mathematical constants are not
/// here: they are not parameters of the model.
/// </remarks>
public struct ModelConstants
{
    // ── build ────────────────────────────────────────────────────────────────
    /// <summary>Voronoi cells smaller than this (px²) are dropped at build.</summary>
    public double MinCellArea;
    /// <summary>Shared sides shorter than this (px) get no bond; the pair is sealed.</summary>
    public double MinSharedEdge;
    /// <summary>Voronoi seed jitter, as a fraction of the cell size.</summary>
    public double SeedJitter;
    /// <summary>The acoustic CFL number the grain floor (<see cref="BodyBuilder.MinGrain"/>) is set to.</summary>
    public float StableCfl;
    /// <summary>Clamp on a bond's structure strength multiplier.</summary>
    public float BondStrengthMin, BondStrengthMax;
    /// <summary>The weakest a bond across the bedding grain can be, as a fraction of nominal.</summary>
    public float AnisoFloor;
    /// <summary>A bond whose cell has fewer bonds than this is treated as at the surface for flaws.</summary>
    public int SurfFlawNeighbours;

    // ── broadphase ───────────────────────────────────────────────────────────
    /// <summary>Rim radius (px) that turns a body's spin into broadphase reach.</summary>
    public float SpinReach;
    /// <summary>Per-body pair margin: base (px) and cap (px).</summary>
    public float BodyMarginBase, BodyMarginMax;
    /// <summary>Grid cell margin: base (px) and cap (px).</summary>
    public float GridMarginBase, GridMarginMax;
    /// <summary>Smallest grid cell (px), and the grid cell as a multiple of the largest cell size.</summary>
    public float GridCellMin, GridCellFactor;
    /// <summary>Rim radius for manifold drift, as a multiple of the body's radius of gyration.</summary>
    public float ManifoldRimFactor;

    // ── contact ──────────────────────────────────────────────────────────────
    /// <summary>Penetration (px) the contact tolerates before the bias, the pressure's confining term,
    /// the backstop floor and the rubble push engage.</summary>
    public float ContactSlop;
    /// <summary>A cell's representative face length, as a fraction of its perimeter: the contact
    /// length in the compliance and the pressure, and the length a recession is spread over.</summary>
    public float FaceLength;
    /// <summary>Penetration past this fraction of the smaller circumradius switches the contact normal
    /// from the SAT axis to the centre-to-centre direction.</summary>
    public float DeepOverlapNormal;
    /// <summary>Rubble positional push: the most penetration (px) corrected per tick, and the share.</summary>
    public float RubblePushMax, RubblePushFraction;

    // ── damage ───────────────────────────────────────────────────────────────
    /// <summary>Damage cap for a bond whose side touches no open side: interior bonds soften but
    /// cannot separate until a crack reaches them.</summary>
    public float InteriorDamageCap;
    /// <summary>At or below this softening ratio a bond snaps at its peak stretch.</summary>
    public float BrittleChi;
    /// <summary>Floor on a bond's softening ratio.</summary>
    public float MinChi;
    /// <summary>Floor on <see cref="SimTuning.ShearMul"/>.</summary>
    public float MinShearMul;

    // ── carving ──────────────────────────────────────────────────────────────
    /// <summary>Largest share of a cell's area one contact may remove in one substep.</summary>
    public float DentAreaGuard;
    /// <summary>v1 clip: largest recession per substep, as a fraction of the cell's circumradius.</summary>
    public float V1DepthCap;
    /// <summary>v1 clip: share of a bond's length a cut may not pass.</summary>
    public float BondedGuardKeep;
    /// <summary>Floor on a carved cell's inertia, as a fraction of <c>m r²</c>.</summary>
    public float InertiaFloor;
    /// <summary>v1 clip: bisection steps placing the plane.</summary>
    public int ClipBisections;
    /// <summary>Dent budget: re-solve passes that redistribute what capped candidates could not take.</summary>
    public int SlideSolvePasses;

    // ── topology ─────────────────────────────────────────────────────────────
    /// <summary>With <see cref="SimTuning.ExportFreeDebris"/>, ticks a lone cell must go untouched
    /// before it is exported.</summary>
    public int DustFreeTicks;

    // ── tolerances ───────────────────────────────────────────────────────────
    /// <summary>Build: consecutive Voronoi vertices closer than this (px) are merged.</summary>
    public double SliverSide;
    /// <summary>Build: two sides are collinear for bonding if within this (px).</summary>
    public double EdgeOverlap;
    /// <summary>Build: a side lies on a seed bisector for labelling within max(abs, rel × radius).</summary>
    public double LabelMatchAbs, LabelMatchRel;
    /// <summary>Build: a side lies on a bisector for touch records within max(abs, rel × radius).</summary>
    public double SideLineAbs, SideLineRel;
    /// <summary>Build: a record end sits on a free side's vertex within this × max(1, radius).</summary>
    public double FreeSideEnd;
    /// <summary>Build: shortest record span kept, and least overlap for a side to link to a record.</summary>
    public double BuildSpanMin, LinkOverlapMin;
    /// <summary>Runtime geometric noise floor (px): zero-length sides, split vertices, coverage snap.</summary>
    public float GeometryNoise;
    /// <summary>Runtime relative tolerance, × the cell's circumradius.</summary>
    public float NoiseRel;
    /// <summary>Absolute floor (px) for re-linking a carve face to the record it lies on.</summary>
    public float RelinkAbs;
    /// <summary>A record span at or below this (px) is spent; an overlap at or below it does not count.</summary>
    public float SpanEpsilon;
}
