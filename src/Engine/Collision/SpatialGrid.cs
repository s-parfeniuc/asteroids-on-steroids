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
///
/// Dedup across cells is O(1) per candidate via a per-query stamp (see <see cref="_stamp"/>),
/// so queries stay cheap as candidate counts grow. Not thread-safe: one game thread rebuilds
/// and queries it (bullets raycast sequentially).
/// </summary>
public sealed class SpatialGrid : ISpatialIndex
{
    private readonly float _cellSize;
    private readonly Dictionary<long, List<Entity>> _cells = new();

    // Pool of lists to avoid allocating new ones every frame.
    private readonly Stack<List<Entity>> _pool = new();

    // Per-query dedup: _stamp[entityId] == _query means "already added to the current result set".
    // Grown on demand; the counter advances each query so no clearing is needed.
    private int[] _stamp = new int[256];
    private int   _query;

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

        BeginQuery();
        for (int cx = minCx; cx <= maxCx; cx++)
        for (int cy = minCy; cy <= maxCy; cy++)
            if (_cells.TryGetValue(CellKey(cx, cy), out var list))
                AppendUnique(list, results);
    }

    public void QuerySegment(Vector2 from, Vector2 to, List<Entity> results)
    {
        BeginQuery();

        // Amanatides–Woo grid DDA: step from cell to cell along the segment, visiting only the
        // cells it actually crosses (not the whole segment-AABB rectangle).
        int cx = CellCoord(from.X), cy = CellCoord(from.Y);
        int ex = CellCoord(to.X),   ey = CellCoord(to.Y);

        VisitCell(cx, cy, results);
        if (cx == ex && cy == ey) return;

        float dx = to.X - from.X, dy = to.Y - from.Y;
        int stepX = dx > 0 ? 1 : -1;
        int stepY = dy > 0 ? 1 : -1;

        // Parametric distance (t in [0,1] along the segment) to the next cell boundary / per cell.
        float tMaxX = dx != 0f ? NextBoundaryT(from.X, dx, cx, stepX) : float.PositiveInfinity;
        float tMaxY = dy != 0f ? NextBoundaryT(from.Y, dy, cy, stepY) : float.PositiveInfinity;
        float tDeltaX = dx != 0f ? MathF.Abs(_cellSize / dx) : float.PositiveInfinity;
        float tDeltaY = dy != 0f ? MathF.Abs(_cellSize / dy) : float.PositiveInfinity;

        // Guard against pathological inputs (both deltas infinite handled above).
        int guard = 0, maxSteps = 1 + Math.Abs(ex - cx) + Math.Abs(ey - cy);
        while (guard++ <= maxSteps)
        {
            if (tMaxX < tMaxY) { cx += stepX; tMaxX += tDeltaX; }
            else               { cy += stepY; tMaxY += tDeltaY; }

            VisitCell(cx, cy, results);
            if (cx == ex && cy == ey) return;
        }
    }

    // Distance along the segment (parameter t) to the first cell boundary in the step direction.
    private float NextBoundaryT(float origin, float d, int cell, int step)
    {
        float boundary = (step > 0 ? cell + 1 : cell) * _cellSize;
        return (boundary - origin) / d;
    }

    private void VisitCell(int cx, int cy, List<Entity> results)
    {
        if (_cells.TryGetValue(CellKey(cx, cy), out var list))
            AppendUnique(list, results);
    }

    // ── Dedup ────────────────────────────────────────────────────────────────
    private void BeginQuery() => _query++;

    private void AppendUnique(List<Entity> list, List<Entity> results)
    {
        foreach (var e in list)
        {
            int id = e.Id;
            if (id >= _stamp.Length) GrowStamp(id);
            if (_stamp[id] == _query) continue;   // already added this query
            _stamp[id] = _query;
            results.Add(e);
        }
    }

    private void GrowStamp(int id)
    {
        int n = _stamp.Length;
        while (n <= id) n *= 2;
        Array.Resize(ref _stamp, n);
    }

    private int CellCoord(float v) => (int)MathF.Floor(v / _cellSize);

    // Pack two 32-bit cell coords into one 64-bit key.
    private static long CellKey(int cx, int cy) =>
        ((long)(uint)cx << 32) | (uint)cy;
}
