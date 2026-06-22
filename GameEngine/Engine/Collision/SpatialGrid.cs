using System.Numerics;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// Uniform spatial hash grid. The world is divided into cells of fixed size.
/// Each entity registers in every cell its AABB overlaps.
/// Candidate lookup returns entities sharing at least one cell.
///
/// Cell size should be ≈ 1.5× the diameter of the largest commonly-spawned entity.
/// Too small → many cells per entity, more bookkeeping.
/// Too large → many entities per cell, more narrow-phase checks.
/// </summary>
public sealed class SpatialGrid : ISpatialIndex
{
    private readonly float _cellSize;
    private readonly Dictionary<long, List<Entity>> _cells = new();

    // Pool of lists to avoid allocating new ones every frame.
    private readonly Stack<List<Entity>> _pool = new();

    public SpatialGrid(float cellSize = 128f)
    {
        _cellSize = cellSize;
    }

    public void Clear()
    {
        foreach (var list in _cells.Values)
        {
            list.Clear();
            _pool.Push(list);
        }
        _cells.Clear();
    }

    public void Insert(Entity entity, Vector2 min, Vector2 max)
    {
        int minCx = CellCoord(min.X);
        int minCy = CellCoord(min.Y);
        int maxCx = CellCoord(max.X);
        int maxCy = CellCoord(max.Y);

        for (int cx = minCx; cx <= maxCx; cx++)
        for (int cy = minCy; cy <= maxCy; cy++)
        {
            long key = CellKey(cx, cy);
            if (!_cells.TryGetValue(key, out var list))
            {
                list = _pool.Count > 0 ? _pool.Pop() : new List<Entity>();
                _cells[key] = list;
            }
            list.Add(entity);
        }
    }

    public void GetCandidates(Vector2 min, Vector2 max, List<Entity> results)
    {
        int minCx = CellCoord(min.X);
        int minCy = CellCoord(min.Y);
        int maxCx = CellCoord(max.X);
        int maxCy = CellCoord(max.Y);

        for (int cx = minCx; cx <= maxCx; cx++)
        for (int cy = minCy; cy <= maxCy; cy++)
        {
            if (_cells.TryGetValue(CellKey(cx, cy), out var list))
                foreach (var e in list)
                    if (!results.Contains(e))   // deduplicate (entity in multiple cells)
                        results.Add(e);
        }
    }

    private int CellCoord(float v) => (int)MathF.Floor(v / _cellSize);

    // Pack two 32-bit cell coords into one 64-bit key.
    private static long CellKey(int cx, int cy) =>
        ((long)(uint)cx << 32) | (uint)cy;
}
