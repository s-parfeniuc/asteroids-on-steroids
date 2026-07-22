using System.Numerics;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// One convex Voronoi cell of a fracturable body, expressed in body-local space
/// (centroid-relative, matching PolygonShape). The union of a body's cells is its
/// (possibly concave) silhouette; each cell on its own is always convex.
/// </summary>
public struct Cell
{
    public Vector2[] Local = null!; // convex polygon, body-local vertices
    public Vector2   Centroid;     // body-local centroid of this cell
    public float     Area;         // |area| of the cell (px²)
    public float     DensityMult = 1f;  // per-cell density multiplier (default 1 = material density).
                                        // Cell mass = Area × DensityMult × material Density; this is the
                                        // single resistance-to-vaporization axis (armor = dense + heavy).
    public float     Damage      = 0f;  // accumulated comminution toward the vaporise threshold
                                        // (cellToughness × mass); repeated hits build it (fatigue),
                                        // it decays by RelaxRate, and the cell pulverises when it's reached.
    /// <summary>Functional role assigned by the shape editor (cockpit, cannon, propeller, etc.).
    /// Null / empty = generic. Preserved through fracture splits.</summary>
    public string?   Role        = null;
    /// <summary>Baked fill colour (role tint + density + rim shading). Computed once when the body is
    /// first built and carried through fracture, so a body's cells never recolour when it takes damage.
    /// Alpha 0 = not yet baked. (Rendering-only; ignored by the physics.)</summary>
    public Color     FillColor   = default;

    public Cell() { }  // required by C# when struct has field initializers (CS8983)
}
