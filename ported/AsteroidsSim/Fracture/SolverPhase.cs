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
    /// <summary>Narrow phase: skinned polygons through SAT. Once per TICK.</summary>
    BuildContacts,
    /// <summary>Per-substep contact refresh: depth from rigid motion, no narrow phase.</summary>
    RefreshContacts,
    /// <summary>Centrifugal, Euler and Coriolis loads, applied before the solve.</summary>
    Inertial,
    /// <summary>Bond forces from the current stretch into the deviation field.</summary>
    BondForces,
    /// <summary>Contact constraints, Gauss-Seidel in build order.</summary>
    Contacts,
    /// <summary>Stretch integrated from the updated deviation field.</summary>
    BondIntegrate,
    /// <summary>Rigid decomposition, plus the Euler torque cancellation.</summary>
    Decompose,
    /// <summary>Deformation realized into u, the cap, damping and body integration.</summary>
    Realize,
    /// <summary>Plastic flow and cohesive damage.</summary>
    Damage,
    /// <summary>Connected-component re-partition after a break.</summary>
    Split,
    /// <summary>Deformed-inertia update, rubble separation and the plastic rebake.</summary>
    Settle,
    /// <summary>The comminution classifier.</summary>
    Dust,

    Count,
}
