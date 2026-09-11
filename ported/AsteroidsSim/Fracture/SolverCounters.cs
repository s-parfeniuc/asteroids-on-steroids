namespace AsteroidsSim.Fracture;

/// <summary>
/// Per-tick work counters. Diagnostics only — nothing in the simulation reads them, and they are
/// not part of the state, so they never affect determinism.
/// </summary>
/// <remarks>
/// <para>The purpose is to separate <i>work that produced an outcome</i> from <i>work spent
/// discovering that there was no outcome</i>. Almost every optimisation available to this solver is
/// of the second kind: a test whose result could have been known in advance. The naming follows
/// that split — <c>*Examined</c> counts entries into a decision, <c>*Reject*</c> counts the ones
/// that fell out at each gate, and the surviving count is what actually did something.</para>
/// </remarks>
public struct SolverCounters
{
    // ── broad phase (BuildPairs) ─────────────────────────────────────────────
    public long PairBuilds;          // how many times the pair list was rebuilt this tick
    public long PairGridInserts;     // cells hashed into the grid
    public long PairBucketVisits;    // candidate cells examined in the 3x3 neighbourhood scan
    public long PairRejectSameBody;
    public long PairRejectOrder;     // the o <= c dedupe
    public long PairRejectRadius;
    public long PairEmitted;

    // ── narrow phase (BuildContacts) ─────────────────────────────────────────
    public long NarrowCalls;         // manifold builds: once per tick plus each drift rebuild
    public long ManifoldRebuilds;    // of those, the ones forced mid-tick by body motion
    public long NarrowExamined;      // pairs walked
    public long NarrowRejectDead;
    public long NarrowRejectSameBody;
    public long NarrowRejectRadius;
    public long NarrowRejectAabb;
    public long SatCalls;
    public long SatSeparated;        // returned "no contact" — the work we would love to skip
    public long SatContact;
    public long SatAxesTested;       // edge normals actually evaluated
    public long SatProjections;      // vertex-onto-axis dot products
    public long SkinComputed;        // cells actually skinned
    public long SkinCacheHit;
    public long LocalComputed;       // cells whose local vertices were transformed
    public long LocalCacheHit;

    // ── bonds ────────────────────────────────────────────────────────────────
    public long BondForceVisits;
    public long BondForceSkipped;    // broken or dead
    public long BondIntegrateVisits;
    public long DamageVisits;
    public long DamageEarlyOut;      // the L1 bound said "cannot act"
    public long DamageFlowed;        // plastic flow occurred
    public long DamageEvaluated;     // reached the cohesive law
    public long DamageBroke;

    // ── contacts ─────────────────────────────────────────────────────────────
    public long ContactSolves;
    public long ContactFriction;
    public long ContactRepeatPair;   // same pair as the previous substep — caching potential

    // ── topology ─────────────────────────────────────────────────────────────
    public long SplitCalls;
    public long SplitNoChange;       // the component walk found nothing new: pure waste
    public long SplitPerformed;
    public long SplitCellsWalked;
    public long SplitBondsReanchored;
    public long RebakeCalls;
    public long RebakeBondSweeps;
    public long DustScans;
    public long DustSinglesSeen;
    public long DustTransfers;
    public long DustConverted;

    // ── other per-cell / per-body passes ─────────────────────────────────────
    public long InertialBodies;
    public long InertialSkipped;     // body not rotating: whole pass skipped
    public long InertialCells;
    public long DecomposeBodies;
    public long DecomposeCells;
    public long DecomposeNoOp;       // field was already zero-mean
    public long RealizeCells;
    public long RealizeCapHit;
    public long UpdateCentersCalls;
    public long UpdateCentersCells;
    public long ReindexCalls;
    public long ReindexBonds;

    public void Reset() => this = default;

    /// <summary>
    /// Folds another set in. Used to merge the per-chunk counters a parallel pass produces, on the
    /// calling thread and in chunk order — so the census is as reproducible as the simulation.
    /// Integer addition, so the order could not matter here anyway; keeping to it is what makes the
    /// rule easy to state for the float reductions that will come later.
    /// </summary>
    public void Add(in SolverCounters o)
    {
        PairBuilds += o.PairBuilds;
        PairGridInserts += o.PairGridInserts;
        PairBucketVisits += o.PairBucketVisits;
        PairRejectSameBody += o.PairRejectSameBody;
        PairRejectOrder += o.PairRejectOrder;
        PairRejectRadius += o.PairRejectRadius;
        PairEmitted += o.PairEmitted;
        NarrowCalls += o.NarrowCalls;
        ManifoldRebuilds += o.ManifoldRebuilds;
        NarrowExamined += o.NarrowExamined;
        NarrowRejectDead += o.NarrowRejectDead;
        NarrowRejectSameBody += o.NarrowRejectSameBody;
        NarrowRejectRadius += o.NarrowRejectRadius;
        NarrowRejectAabb += o.NarrowRejectAabb;
        SatCalls += o.SatCalls;
        SatSeparated += o.SatSeparated;
        SatContact += o.SatContact;
        SatAxesTested += o.SatAxesTested;
        SatProjections += o.SatProjections;
        SkinComputed += o.SkinComputed;
        SkinCacheHit += o.SkinCacheHit;
        LocalComputed += o.LocalComputed;
        LocalCacheHit += o.LocalCacheHit;
        BondForceVisits += o.BondForceVisits;
        BondForceSkipped += o.BondForceSkipped;
        BondIntegrateVisits += o.BondIntegrateVisits;
        DamageVisits += o.DamageVisits;
        DamageEarlyOut += o.DamageEarlyOut;
        DamageFlowed += o.DamageFlowed;
        DamageEvaluated += o.DamageEvaluated;
        DamageBroke += o.DamageBroke;
        ContactSolves += o.ContactSolves;
        ContactFriction += o.ContactFriction;
        ContactRepeatPair += o.ContactRepeatPair;
        SplitCalls += o.SplitCalls;
        SplitNoChange += o.SplitNoChange;
        SplitPerformed += o.SplitPerformed;
        SplitCellsWalked += o.SplitCellsWalked;
        SplitBondsReanchored += o.SplitBondsReanchored;
        RebakeCalls += o.RebakeCalls;
        RebakeBondSweeps += o.RebakeBondSweeps;
        DustScans += o.DustScans;
        DustSinglesSeen += o.DustSinglesSeen;
        DustTransfers += o.DustTransfers;
        DustConverted += o.DustConverted;
        InertialBodies += o.InertialBodies;
        InertialSkipped += o.InertialSkipped;
        InertialCells += o.InertialCells;
        DecomposeBodies += o.DecomposeBodies;
        DecomposeCells += o.DecomposeCells;
        DecomposeNoOp += o.DecomposeNoOp;
        RealizeCells += o.RealizeCells;
        RealizeCapHit += o.RealizeCapHit;
        UpdateCentersCalls += o.UpdateCentersCalls;
        UpdateCentersCells += o.UpdateCentersCells;
        ReindexCalls += o.ReindexCalls;
        ReindexBonds += o.ReindexBonds;
    }
}
