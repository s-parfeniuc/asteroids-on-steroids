namespace AsteroidsSim.Fracture;

/// <summary>
/// The phases of one tick, in execution order. Used only for profiling — the simulation's
/// behaviour does not depend on these, but the ORDER they appear in does, so the enum doubles as
/// documentation of the tick.
/// </summary>
public enum SolverPhase
{
    /// <summary>Candidate pair list, rebuilt once per tick with a speed-dependent margin.</summary>
    BuildPairs = 0,
    /// <summary>Narrow phase: world polygons through SAT. Once per tick, again only on drift.</summary>
    BuildContacts,
    /// <summary>Per-substep contact refresh: depth from rigid motion, no narrow phase.</summary>
    RefreshContacts,
    /// <summary>Centrifugal, Euler and Coriolis loads, applied before the solve.</summary>
    Inertial,
    /// <summary>Bond forces from the current stretch into the deviation field.</summary>
    BondForces,
    /// <summary>Contact constraints, Gauss-Seidel in build order, then carving.</summary>
    Contacts,
    /// <summary>Stretch integrated from the updated deviation field.</summary>
    BondIntegrate,
    /// <summary>Rigid decomposition, plus the Euler torque cancellation.</summary>
    Decompose,
    /// <summary>Damping of the deviation field and body integration.</summary>
    Damping,
    /// <summary>Cohesive damage and bond separation.</summary>
    Damage,
    /// <summary>Connected-component re-partition after a break, and the rigid decomposition that follows.</summary>
    Split,
    /// <summary>Positional separation of touching bond-less single cells.</summary>
    Settle,
    /// <summary>Comminution: removing cells carving has run out or the backstop queued.</summary>
    Dust,

    Count,
}
