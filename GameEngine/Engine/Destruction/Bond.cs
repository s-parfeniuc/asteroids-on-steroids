namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// A cohesive bond between two adjacent cells that share a Voronoi edge. The crack
/// propagation spends an energy budget breaking bonds; connected-component analysis
/// over the surviving bonds yields the fragments.
/// </summary>
public struct Bond
{
    public int   A;           // cell index
    public int   B;           // cell index
    public float EdgeLength;  // length of the shared edge
    public float Strength;    // energy to break = EdgeLength × material Toughness
}
