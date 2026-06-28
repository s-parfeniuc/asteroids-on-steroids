using System.Numerics;
using AsteroidsEngine.Engine.Destruction;

namespace AsteroidsGame.Gameplay;

/// <summary>
/// Pure geometry: derives a fracturable body's render edges from its cells + bonds —
/// the silhouette (unshared edges) and the crack lines (shared-but-unbonded edges).
/// Shared by the game and the demo renderers and spawners.
/// </summary>
public static class FractureMesh
{
    /// <summary>Static outline + cracks for an intact (spawn-time) body.</summary>
    public static (Vector2[] outline, Vector2[] cracks) ComputeEdges(Cell[] cells, Bond[] bonds)
    {
        var bonded = new HashSet<(int, int)>();
        foreach (var b in bonds) bonded.Add((Math.Min(b.A, b.B), Math.Max(b.A, b.B)));

        // edge-midpoint key → the up-to-two cells that own that edge
        var edgeCells = new Dictionary<(int, int), (int a, int b)>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 mid = (v[i] + v[(i + 1) % n]) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                edgeCells[key] = edgeCells.TryGetValue(key, out var p) ? (p.a, ci) : (ci, -1);
            }
        }

        var outline = new List<Vector2>(); var cracks = new List<Vector2>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % n];
                Vector2 mid = (a + b) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                var (c0, c1) = edgeCells[key];
                if (c1 < 0) { outline.Add(a); outline.Add(b); }                       // unshared → silhouette
                else if (!bonded.Contains((Math.Min(c0, c1), Math.Max(c0, c1))))      // shared but not bonded → crack
                {
                    if (ci == Math.Min(c0, c1)) { cracks.Add(a); cracks.Add(b); }     // dedupe (edge is in both cells)
                }
                // else bonded shared edge → hidden
            }
        }
        return (outline.ToArray(), cracks.ToArray());
    }

    /// <summary>Outline + cracks for a live-fracturing body, honouring the per-frame
    /// broken-bond and pulverised-cell state (pulverised cells are excluded entirely).</summary>
    public static (Vector2[] outline, Vector2[] cracks) ComputeEdgesLive(
        Cell[] cells, Bond[] bonds, bool[] broken, bool[] pulverized)
    {
        var bonded = new HashSet<(int, int)>();
        for (int bi = 0; bi < bonds.Length; bi++)
        {
            if (broken[bi]) continue;
            int a = bonds[bi].A, b = bonds[bi].B;
            if (!pulverized[a] && !pulverized[b])
                bonded.Add((Math.Min(a, b), Math.Max(a, b)));
        }
        var edgeCells = new Dictionary<(int, int), (int a, int b)>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            if (pulverized[ci]) continue;
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 mid = (v[i] + v[(i + 1) % n]) * 0.5f;
                var key = ((int)MathF.Round(mid.X * 2f), (int)MathF.Round(mid.Y * 2f));
                edgeCells[key] = edgeCells.TryGetValue(key, out var p) ? (p.a, ci) : (ci, -1);
            }
        }
        var outline = new List<Vector2>(); var cracks = new List<Vector2>();
        for (int ci = 0; ci < cells.Length; ci++)
        {
            if (pulverized[ci]) continue;
            var v = cells[ci].Local; int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % n];
                var key = ((int)MathF.Round(((a + b) * 0.5f).X * 2f),
                           (int)MathF.Round(((a + b) * 0.5f).Y * 2f));
                var (c0, c1) = edgeCells[key];
                if (c1 < 0) { outline.Add(a); outline.Add(b); }
                else if (!bonded.Contains((Math.Min(c0, c1), Math.Max(c0, c1))))
                    if (ci == Math.Min(c0, c1)) { cracks.Add(a); cracks.Add(b); }
            }
        }
        return (outline.ToArray(), cracks.ToArray());
    }
}
