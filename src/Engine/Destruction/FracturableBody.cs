using AsteroidsEngine.Engine.Components;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// Component: a pre-fractured body — convex cells joined by a bond graph.
///
/// A FracturableBody is always a single connected component of cells = one rigid
/// body with one CompoundShape collider. Cracks (bonds removed by a prior hit that
/// did not disconnect the body) accumulate as damage and weaken it for future hits
/// without changing the collision shape until cells actually separate; then the
/// engine produces new fragment bodies.
///
/// See docs/destruction_engine_spec.md §4.2.
/// </summary>
public struct FracturableBody
{
    public Cell[] Cells;
    public Bond[] Bonds;                    // current adjacency (shrinks as cracks form)
    public FractureProperties Material;
    public FractureState      State;
    /// <summary>Total area below the material's MinFragmentArea (and not a cockpit body): any fracture
    /// on it vaporises it whole (one-shot). Set at spawn. Impact/physics on OTHER bodies stay normal.</summary>
    public bool               Fragile;
}
