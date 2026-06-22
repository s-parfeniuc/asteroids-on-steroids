using System.Numerics;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Broad-phase spatial index. Implementations: SpatialGrid (current), Quadtree (future).
/// The index is rebuilt from scratch each frame via Clear() + Insert() calls
/// in CollisionSystem, then queried with GetCandidates().
/// </summary>
public interface ISpatialIndex
{
    void Clear();
    void Insert(Entity entity, Vector2 min, Vector2 max);

    /// <summary>
    /// Returns all entities whose AABB overlaps the query AABB.
    /// May include false positives — narrow phase filters them.
    /// The returned list is owned by the index (do not hold across frames).
    /// </summary>
    void GetCandidates(Vector2 min, Vector2 max, List<Entity> results);
}
