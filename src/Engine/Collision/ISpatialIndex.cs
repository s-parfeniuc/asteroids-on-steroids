using System.Numerics;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Broad-phase spatial index. Implementations: SpatialGrid (current), Quadtree (future).
/// The index is rebuilt from scratch each frame by <see cref="BroadPhaseSystem"/> (Clear() +
/// Insert()), then queried — by AABB (<see cref="GetCandidates"/>) for collision and by ray
/// segment (<see cref="QuerySegment"/>) for raycast bullets. One shared instance serves both.
/// </summary>
public interface ISpatialIndex
{
    void Clear();
    void Insert(Entity entity, Vector2 min, Vector2 max);

    /// <summary>
    /// Returns all entities whose AABB overlaps the query AABB.
    /// May include false positives — narrow phase filters them.
    /// The returned list is owned by the caller; the index appends deduplicated entries.
    /// </summary>
    void GetCandidates(Vector2 min, Vector2 max, List<Entity> results);

    /// <summary>
    /// Appends every entity in a cell the segment from→to passes through (deduplicated), so a
    /// raycast only narrow-tests bodies near its path. Broad-phase only — may include false
    /// positives that the exact per-entity raycast then rejects.
    /// </summary>
    void QuerySegment(Vector2 from, Vector2 to, List<Entity> results);
}
