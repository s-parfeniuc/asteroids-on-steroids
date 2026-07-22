namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Assigned to all live entities produced by a single fracture event (fragments,
/// debris excluded, and the surviving/continuing body). Entities that share the same
/// Id are fracture siblings: positional correction is suppressed between them so a
/// just-detached piece cannot push its former parent in the wrong direction.
/// Normal velocity solving is unaffected — siblings still bounce off each other.
/// FramesLeft counts down each fixed step; the component is removed at zero.
/// Inherited on re-split so multi-generational pieces stay in the same group.
/// </summary>
public struct FractureGroup
{
    public int Id;
    public int FramesLeft;
}
