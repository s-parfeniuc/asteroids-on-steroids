using System;

namespace AsteroidsSim.Fracture;

public sealed partial class Solver
{
    /// <summary>
    /// The most vertices any live cell has, so a renderer can size its buffers once.
    /// </summary>
    public int MaxPolyLen
    {
        get
        {
            int m = 0;
            for (int c = 0; c < _s.CellCount; c++)
                if (!_s.Dead(c) && _s.PolyLen[c] > m) m = _s.PolyLen[c];
            return m;
        }
    }

    /// <summary>
    /// One cell's collider polygon in <b>body-local</b> space, for drawing.
    /// </summary>
    /// <remarks>
    /// <para>Body-local so the caller can draw at a pose interpolated between the previous tick and
    /// the current one without re-deriving geometry: one rotation per body per frame.</para>
    ///
    /// <para>This is the rest offset plus the rest polygon — constant until a carve or a split
    /// changes it — so two cells sharing a Voronoi corner place it at the same point by
    /// construction.</para>
    /// </remarks>
    /// <returns>Vertex count written, or 0 for a dead cell.</returns>
    public int CellLocalPolygon(int cell, Span<float> outX, Span<float> outY)
    {
        if (_s.Dead(cell)) return 0;
        int off = _s.PolyOff[cell], len = _s.PolyLen[cell];
        if (len > outX.Length || len > outY.Length) return 0;

        float rx = _s.CellRx[cell], ry = _s.CellRy[cell];
        for (int v = 0; v < len; v++)
        {
            outX[v] = rx + _s.PolyX[off + v];
            outY[v] = ry + _s.PolyY[off + v];
        }
        return len;
    }
}
