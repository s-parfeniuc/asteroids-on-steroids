using System.Numerics;

namespace AsteroidsEngine.Engine.Collision;

/// <summary>
/// A collision shape composed of multiple convex child shapes (e.g. an asteroid's
/// cells). Enables concave-capable collision without modifying the SAT pipeline:
/// each part dispatches through the existing double-dispatch table.
///
/// All parts share the entity's Transform (pos/rot); their geometry is expressed
/// in the compound's local space (centroid-relative), matching PolygonShape.
///
/// Internal broad-phase: each part's local-space AABB is precomputed once. A test
/// against another shape transforms that shape's world AABB into this compound's
/// local frame and skips parts that cannot overlap — so a 100-cell body costs a
/// linear scan of cheap AABB tests plus a few SAT tests, not 100 SAT tests.
///
/// Thread safety: LastHitPartIndex is written during a test and read immediately
/// after in the single-threaded fracture handler. Do not cache across frames.
/// </summary>
public sealed class CompoundShape : CollisionShape
{
    private readonly CollisionShape[]              _parts;
    private readonly (Vector2 min, Vector2 max)[]  _localAabbs;   // per-part, compound-local
    private readonly (Vector2 min, Vector2 max)    _localBounds;  // union (for O(1) GetAABB)
    private readonly bool[]                         _disabled;     // pulverized cells: skipped by all tests

    public int PartCount => _parts.Length;

    /// <summary>Permanently removes a part from collision (e.g. its cell pulverized into a crater):
    /// every intersection/raycast/manifold test skips it, so projectiles and bodies pass through the
    /// hole. Idempotent; out-of-range indices are ignored.</summary>
    public void DisablePart(int index)
    {
        if ((uint)index < (uint)_disabled.Length) _disabled[index] = true;
    }

    public bool IsPartDisabled(int index) =>
        (uint)index < (uint)_disabled.Length && _disabled[index];

    /// <summary>
    /// Index into _parts of the child that produced the deepest contact in the
    /// most recent test. -1 if no contact was found.
    /// </summary>
    public int LastHitPartIndex { get; private set; } = -1;

    public CompoundShape(CollisionShape[] parts)
    {
        if (parts.Length == 0) throw new ArgumentException("CompoundShape needs at least one part.");
        _parts = parts;
        _disabled = new bool[parts.Length];

        _localAabbs = new (Vector2, Vector2)[parts.Length];
        var bmin = new Vector2(float.MaxValue);
        var bmax = new Vector2(float.MinValue);
        for (int i = 0; i < parts.Length; i++)
        {
            // Part geometry is already compound-local, so identity transform = local AABB.
            var aabb = parts[i].GetAABB(Vector2.Zero, 0f);
            _localAabbs[i] = aabb;
            bmin = Vector2.Min(bmin, aabb.min);
            bmax = Vector2.Max(bmax, aabb.max);
        }
        _localBounds = (bmin, bmax);
    }

    public CollisionShape GetPart(int index) => _parts[index];

    /// <summary>Returns a new CompoundShape with the part at index removed.</summary>
    public CompoundShape WithoutPart(int index)
    {
        var remaining = new CollisionShape[_parts.Length - 1];
        int j = 0;
        for (int i = 0; i < _parts.Length; i++)
            if (i != index) remaining[j++] = _parts[i];
        return new CompoundShape(remaining);   // ctor recomputes the broad-phase data
    }

    // -------------------------------------------------------------------------
    // Broad-phase helpers
    // -------------------------------------------------------------------------

    /// <summary>Conservative axis-aligned bounds of a world AABB after transforming
    /// into this compound's local frame (un-rotate + un-translate).</summary>
    private static (Vector2 min, Vector2 max) WorldAabbToLocal(
        Vector2 wmin, Vector2 wmax, Vector2 pos, float rot)
    {
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 ToLocal(Vector2 w)
        {
            Vector2 d = w - pos;
            return new Vector2(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos);
        }

        Vector2 c0 = ToLocal(wmin);
        Vector2 c1 = ToLocal(new Vector2(wmax.X, wmin.Y));
        Vector2 c2 = ToLocal(new Vector2(wmin.X, wmax.Y));
        Vector2 c3 = ToLocal(wmax);
        return (Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3)),
                Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3)));
    }

    private static bool Overlaps((Vector2 min, Vector2 max) a, Vector2 bmin, Vector2 bmax) =>
        a.min.X <= bmax.X && a.max.X >= bmin.X &&
        a.min.Y <= bmax.Y && a.max.Y >= bmin.Y;

    // -------------------------------------------------------------------------
    // CompoundShape as entity A (primary shape)
    // -------------------------------------------------------------------------

    public override ContactInfo? Intersects(Vector2 posA, float rotA,
                                            CollisionShape other,
                                            Vector2 posB, float rotB)
    {
        var (wmin, wmax) = other.GetAABB(posB, rotB);
        var (qmin, qmax) = WorldAabbToLocal(wmin, wmax, posA, rotA);

        ContactInfo? deepest = null;
        LastHitPartIndex = -1;
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i] || !Overlaps(_localAabbs[i], qmin, qmax)) continue;
            var c = _parts[i].Intersects(posA, rotA, other, posB, rotB);
            if (c != null && (deepest == null || c.Value.Depth > deepest.Value.Depth))
            {
                deepest = c;
                LastHitPartIndex = i;
            }
        }
        return deepest;
    }

    /// <summary>
    /// Collects a contact for EACH of this compound's parts that overlaps
    /// <paramref name="other"/> — a contact manifold rather than a single deepest
    /// contact. Each ContactInfo.Normal is oriented by the contacting CELL's geometry
    /// to point from that cell toward <paramref name="other"/> (the direction to push
    /// <paramref name="other"/> away). Using the cell — not the whole compound's
    /// centroid — is what keeps concavities correct: a body sitting in a crater is
    /// pushed out of the wall it actually touches, never "through" the body via a
    /// far-side wall. A manifold is what lets the solver separate multi-cell overlaps.
    /// </summary>
    public void CollectContacts(Vector2 posA, float rotA, CollisionShape other,
                                Vector2 posB, float rotB, List<ContactInfo> outList)
    {
        var (wmin, wmax) = other.GetAABB(posB, rotB);
        var (qmin, qmax) = WorldAabbToLocal(wmin, wmax, posA, rotA);
        float cos = MathF.Cos(rotA), sin = MathF.Sin(rotA);
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i] || !Overlaps(_localAabbs[i], qmin, qmax)) continue;
            var c = _parts[i].Intersects(posA, rotA, other, posB, rotB);
            if (c == null) continue;

            // Which cell of the OTHER body did this part touch? A compound records the deepest
            // part it matched during the test just above, so both sides of the contact are known
            // and fracture can seed the cell actually struck rather than guessing from the point.
            int otherPart = other is CompoundShape oc ? oc.LastHitPartIndex : -1;

            var (amin, amax) = _localAabbs[i];
            Vector2 lc = (amin + amax) * 0.5f;                       // cell centre (local)
            Vector2 cellWorld = new(lc.X * cos - lc.Y * sin + posA.X,
                                    lc.X * sin + lc.Y * cos + posA.Y);
            var ci = c.Value.WithParts(i, otherPart);
            if (Vector2.Dot(ci.Normal, posB - cellWorld) < 0f) ci = ci.Flipped();
            outList.Add(ci);
        }
    }

    public override (Vector2 min, Vector2 max) GetAABB(Vector2 pos, float rot)
    {
        // O(1): transform the four corners of the precomputed local bounds.
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 ToWorld(Vector2 l) =>
            new Vector2(l.X * cos - l.Y * sin + pos.X, l.X * sin + l.Y * cos + pos.Y);

        var (lmin, lmax) = _localBounds;
        Vector2 c0 = ToWorld(lmin);
        Vector2 c1 = ToWorld(new Vector2(lmax.X, lmin.Y));
        Vector2 c2 = ToWorld(new Vector2(lmin.X, lmax.Y));
        Vector2 c3 = ToWorld(lmax);
        return (Vector2.Min(Vector2.Min(c0, c1), Vector2.Min(c2, c3)),
                Vector2.Max(Vector2.Max(c0, c1), Vector2.Max(c2, c3)));
    }

    /// <summary>
    /// Every part the world-space segment from→to passes through, ordered by entry distance along
    /// it. Unlike <see cref="Raycast"/> (which stops at the nearest part) this reports the whole
    /// tunnel, so a penetrating projectile can act on each cell it crosses, in order. A part that
    /// already contains <paramref name="from"/> is reported at T = 0. Disabled parts are skipped.
    /// </summary>
    public void SegmentParts(Vector2 pos, float rot, Vector2 from, Vector2 to,
                             List<(int Part, float T, Vector2 Point)> outList)
    {
        outList.Clear();

        // Work in compound-local space: un-translate then un-rotate both endpoints once.
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 ToLocal(Vector2 w)
        {
            Vector2 d = w - pos;
            return new Vector2(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos);
        }
        Vector2 p0 = ToLocal(from), p1 = ToLocal(to);
        Vector2 seg = p1 - p0;
        if (seg.LengthSquared() < 1e-12f) return;

        Vector2 segMin = Vector2.Min(p0, p1), segMax = Vector2.Max(p0, p1);

        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i]) continue;
            if (!Overlaps(_localAabbs[i], segMin, segMax)) continue;
            if (_parts[i] is not PolygonShape poly) continue;

            var verts = poly.LocalVertices;
            if (verts.Length < 3) continue;

            float t = SegmentEntryT(verts, p0, seg);
            if (t < 0f) continue;
            outList.Add((i, t, from + (to - from) * t));
        }

        outList.Sort(static (a, b) => a.T.CompareTo(b.T));
    }

    /// <summary>Parameter t ∈ [0,1] at which the segment p0+seg·t first enters the polygon, or 0 if
    /// p0 is already inside it. -1 when the segment misses entirely.</summary>
    private static float SegmentEntryT(ReadOnlySpan<Vector2> verts, Vector2 p0, Vector2 seg)
    {
        // Inside at the start → it is already in this cell.
        bool inside = false;
        for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
        {
            if ((verts[i].Y > p0.Y) != (verts[j].Y > p0.Y) &&
                p0.X < (verts[j].X - verts[i].X) * (p0.Y - verts[i].Y) / (verts[j].Y - verts[i].Y) + verts[i].X)
                inside = !inside;
        }
        if (inside) return 0f;

        // Otherwise the earliest edge crossing within the segment.
        float best = float.MaxValue;
        for (int i = 0, j = verts.Length - 1; i < verts.Length; j = i++)
        {
            Vector2 a = verts[j], e = verts[i] - a;
            float denom = seg.X * e.Y - seg.Y * e.X;      // cross(seg, edge)
            if (MathF.Abs(denom) < 1e-9f) continue;        // parallel
            Vector2 ap = a - p0;
            float t = (ap.X * e.Y - ap.Y * e.X) / denom;         // along the segment
            float u = (ap.X * seg.Y - ap.Y * seg.X) / denom;     // along the edge
            if (t >= 0f && t <= 1f && u >= 0f && u <= 1f && t < best) best = t;
        }
        return best <= 1f ? best : -1f;
    }

    public override bool Raycast(Vector2 origin, Vector2 dir, float maxDist,
                                 Vector2 pos, float rot, out RayCastResult hit)
    {
        hit = default;
        bool  any  = false;
        float best = maxDist;

        // Reject cells whose local AABB the ray segment can't cross (mirrors SegmentParts' gate),
        // so a ray through the body only pays for the handful of cells along its path, not all of them.
        float cos = MathF.Cos(rot), sin = MathF.Sin(rot);
        Vector2 ToLocal(Vector2 w)
        {
            Vector2 d = w - pos;
            return new Vector2(d.X * cos + d.Y * sin, -d.X * sin + d.Y * cos);
        }
        Vector2 lp0 = ToLocal(origin), lp1 = ToLocal(origin + dir * maxDist);
        Vector2 segMin = Vector2.Min(lp0, lp1), segMax = Vector2.Max(lp0, lp1);

        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i] || !Overlaps(_localAabbs[i], segMin, segMax)) continue;
            if (_parts[i].Raycast(origin, dir, best, pos, rot, out var h))
            {
                best = h.Distance;
                hit  = new RayCastResult(h.Distance, h.Point, h.Normal, partIndex: i);
                any  = true;
            }
        }
        return any;
    }

    // -------------------------------------------------------------------------
    // CompoundShape as entity B (double-dispatch targets).
    // Each fans across parts, culled by the query's local-frame AABB.
    // -------------------------------------------------------------------------

    internal override ContactInfo? IntersectsCircle(Vector2 posA, float rotA,
                                                    CircleShape circle, Vector2 posB)
    {
        Vector2 r = new(circle.Radius);
        var (qmin, qmax) = WorldAabbToLocal(posB - r, posB + r, posA, rotA);

        ContactInfo? deepest = null;
        LastHitPartIndex = -1;
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i] || !Overlaps(_localAabbs[i], qmin, qmax)) continue;
            var c = _parts[i].IntersectsCircle(posA, rotA, circle, posB);
            if (c != null && (deepest == null || c.Value.Depth > deepest.Value.Depth))
            {
                deepest = c;
                LastHitPartIndex = i;
            }
        }
        return deepest;
    }

    internal override ContactInfo? IntersectsPolygon(Vector2 posA, float rotA,
                                                     PolygonShape polygon,
                                                     Vector2 posB, float rotB)
    {
        var (wmin, wmax) = polygon.GetAABB(posB, rotB);
        var (qmin, qmax) = WorldAabbToLocal(wmin, wmax, posA, rotA);

        ContactInfo? deepest = null;
        LastHitPartIndex = -1;
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i] || !Overlaps(_localAabbs[i], qmin, qmax)) continue;
            var c = _parts[i].IntersectsPolygon(posA, rotA, polygon, posB, rotB);
            if (c != null && (deepest == null || c.Value.Depth > deepest.Value.Depth))
            {
                deepest = c;
                LastHitPartIndex = i;
            }
        }
        return deepest;
    }

    internal override ContactInfo? IntersectsAABB(Vector2 posA, float rotA,
                                                  AABBShape aabb, Vector2 posB)
    {
        var (wmin, wmax) = aabb.GetAABB(posB, 0f);
        var (qmin, qmax) = WorldAabbToLocal(wmin, wmax, posA, rotA);

        ContactInfo? deepest = null;
        LastHitPartIndex = -1;
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_disabled[i] || !Overlaps(_localAabbs[i], qmin, qmax)) continue;
            var c = _parts[i].IntersectsAABB(posA, rotA, aabb, posB);
            if (c != null && (deepest == null || c.Value.Depth > deepest.Value.Depth))
            {
                deepest = c;
                LastHitPartIndex = i;
            }
        }
        return deepest;
    }
}
