using System;
using System.Collections.Generic;

namespace AsteroidsSim.Fracture;

/// <summary>
/// Test and tool scenes: the correctness tests, the determinism fingerprint, the bench and the
/// viewer build from these. Not gameplay content.
/// </summary>
/// <remarks>
/// Body creation order is part of the determinism contract — all bodies in a scene draw from one
/// generator stream, so the order in which they are built decides what every one of them looks
/// like.
/// </remarks>
public static class Scenarios
{
    public readonly struct Result
    {
        public readonly SimState State;
        public readonly Solver Solver;
        public readonly float P0x;
        public readonly float P0y;
        public readonly float Ke0;
        public readonly float TotalMass;
        public readonly float V0;

        public Result(SimState s, Solver solver, float p0x, float p0y, float ke0, float mass, float v0)
        {
            State = s; Solver = solver; P0x = p0x; P0y = p0y; Ke0 = ke0; TotalMass = mass; V0 = v0;
        }

        /// <summary>
        /// Momentum drift as a fraction of the scene's momentum scale, counting material the dust
        /// classifier has exported. Counting live bodies alone mis-reads exported momentum as a leak.
        /// </summary>
        public float MomentumDrift()
        {
            Solver.TotalMomentum(out float px, out float py);
            float dx = px + Solver.ExportedPx - P0x;
            float dy = py + Solver.ExportedPy - P0y;
            float scale = TotalMass * V0;
            if (scale < 1f) scale = 1f;
            return Math.SimMath.Hypot(dx, dy) / scale;
        }

        /// <summary>Kinetic energy as a fraction of the initial, including exported material.</summary>
        public float EnergyFraction()
            => (Solver.BodyKineticEnergy() + Solver.ExportedKe) / Ke0;
    }

    /// <summary>Two comparable asteroids meeting head-on, each with some spin.</summary>
    public static Result Collide(in SimTuning tuning, in Material material,
        float speed = 600f, float grain = 900f, int seed = ProtoRng.DefaultSeed,
        List<Vec2d>? outlineA = null, List<Vec2d>? outlineB = null)
    {
        var s = new SimState();
        var rng = new ProtoRng(seed);

        var a = outlineA ?? BodyBuilder.MakeBlob(ref rng, 250, 350, 130, 120);
        if (outlineA != null) for (int i = 0; i < 20; i++) rng.NextDouble();
        BodyBuilder.AddBody(s, ref rng, tuning, a, speed * 0.5f, 0f, 0.4f, material, grain);

        var b = outlineB ?? BodyBuilder.MakeBlob(ref rng, 650, 350, 130, 120);
        if (outlineB != null) for (int i = 0; i < 20; i++) rng.NextDouble();
        BodyBuilder.AddBody(s, ref rng, tuning, b, -speed * 0.5f, 0f, -0.3f, material, grain);

        return Finish(s, tuning);
    }

    /// <summary>A single spinning body: the idle test. Nothing should ever happen.</summary>
    public static Result Spin(in SimTuning tuning, in Material material,
        float omega = 1.0f, float grain = 900f, int seed = ProtoRng.DefaultSeed)
    {
        var s = new SimState();
        var rng = new ProtoRng(seed);
        var outline = BodyBuilder.MakeBlob(ref rng, 450, 350, 170, 150);
        BodyBuilder.AddBody(s, ref rng, tuning, outline, 0f, 0f, omega, material, grain);
        return Finish(s, tuning);
    }

    /// <summary>A small fast body into a large one.</summary>
    public static Result Projectile(in SimTuning tuning, in Material material,
        float speed = 900f, float massMul = 3f, float grain = 900f,
        int seed = ProtoRng.DefaultSeed, Material? impactor = null)
    {
        var s = new SimState();
        var rng = new ProtoRng(seed);

        var target = BodyBuilder.MakeBlob(ref rng, 560, 350, 190, 175);
        BodyBuilder.AddBody(s, ref rng, tuning, target, 0f, 0f, 0f, material, grain);

        float r = 15f * Math.SimMath.Sqrt(massMul);
        var shot = BodyBuilder.MakeBlob(ref rng, 120, 350, r, r, 0.18, 10);
        BodyBuilder.AddBody(s, ref rng, tuning, shot, speed, 0f, 0f, impactor ?? material, grain);

        return Finish(s, tuning);
    }

    /// <summary>
    /// A projectile into a body built from two materials: a hard shell over a softer core.
    /// </summary>
    /// <remarks>
    /// The reason per-cell material exists. The shell should resist comminution while the core
    /// crushes behind it, and the interface bonds should fail first — a bond takes the weaker of its
    /// two cells on both the strain and the softening axis, so the shell/core seam is the weakest
    /// line in the body even though half of it is steel.
    /// </remarks>
    public static Result Shell(in SimTuning tuning, in Material shell, in Material core,
        float speed = 1500f, float massMul = 8f, float grain = 900f, float shellFrac = 0.30f,
        int seed = ProtoRng.DefaultSeed, Material? impactor = null)
    {
        var s = new SimState();
        var rng = new ProtoRng(seed);

        var target = BodyBuilder.MakeBlob(ref rng, 560, 350, 190, 175);
        // Shell by depth from the outline: a seed within shellFrac of the bounding radius is shell.
        Geometry2D.BBox(target, out double bx0, out double by0, out double bx1, out double by1);
        double ccx = 0.5 * (bx0 + bx1), ccy = 0.5 * (by0 + by1);
        double rad = 0.5 * Math.SimMath.Min((float)(bx1 - bx0), (float)(by1 - by0));
        double inner = rad * (1.0 - shellFrac);
        Material sh = shell, co = core;
        // AddBody only knows the nominal (core) material; the shell's wave speed bounds the grain too.
        grain = System.Math.Max(grain, BodyBuilder.MinGrain(sh, tuning));
        BodyBuilder.AddBody(s, ref rng, tuning, target, 0f, 0f, 0f, core, grain,
            matAt: p => (p.X - ccx) * (p.X - ccx) + (p.Y - ccy) * (p.Y - ccy) >= inner * inner ? sh : co);

        float r = 15f * Math.SimMath.Sqrt(massMul);
        var shot = BodyBuilder.MakeBlob(ref rng, 120, 350, r, r, 0.18, 10);
        BodyBuilder.AddBody(s, ref rng, tuning, shot, speed, 0f, 0f, impactor ?? shell, grain);

        return Finish(s, tuning);
    }

    /// <summary>
    /// A field of asteroids on a grid, drifting so that a realistic fraction of them are in contact
    /// at any moment. This is the scaling scenario: it is not a reference case for behaviour, it
    /// exists to put a chosen number of live cells in front of the solver.
    /// </summary>
    /// <param name="cols">Bodies across.</param>
    /// <param name="rows">Bodies down.</param>
    /// <param name="radius">Body radius; with the grain this sets cells per body.</param>
    /// <param name="spacing">Centre-to-centre spacing. Below ~2.2x the radius they start packed.</param>
    /// <param name="speed">Drift speed scale.</param>
    public static Result Field(in SimTuning tuning, in Material material,
        int cols, int rows, float radius = 60f, float spacing = 150f, float speed = 60f,
        float grain = 900f, int seed = ProtoRng.DefaultSeed)
    {
        var s = new SimState();
        var rng = new ProtoRng(seed);

        float x0 = 400f, y0 = 400f;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                float cx = x0 + c * spacing;
                float cy = y0 + r * spacing;
                var outline = BodyBuilder.MakeBlob(ref rng, cx, cy, radius, radius * 0.92, 0.14, 16);

                // Drift toward the field centre so contacts actually happen, plus a little spin.
                float tx = x0 + (cols - 1) * spacing * 0.5f;
                float ty = y0 + (rows - 1) * spacing * 0.5f;
                float dx = tx - cx, dy = ty - cy;
                float L = Math.SimMath.Hypot(dx, dy);
                if (L < 1e-3f) L = 1f;
                float jitter = (float)rng.Range(0.6, 1.4);
                float spin = (float)rng.Range(-0.8, 0.8);

                BodyBuilder.AddBody(s, ref rng, tuning, outline,
                    dx / L * speed * jitter, dy / L * speed * jitter, spin, material, grain);
            }
        }
        return Finish(s, tuning);
    }

    /// <summary>
    /// What a round of this shape and material would weigh, for callers that want to keep a round's
    /// mass while changing its size. Builds the same blob <see cref="FireAt"/> would from the same
    /// seed, on its own rng, so it neither needs nor disturbs the caller's stream.
    /// </summary>
    public static float RoundMass(in Material material, float radius,
        float radiusX = 0f, float radiusY = 0f, int seed = 1)
    {
        var rng = new ProtoRng(seed);
        float rx = radiusX > 0f ? radiusX : radius, ry = radiusY > 0f ? radiusY : radius;
        bool rod = radiusX > 0f && radiusY > 0f && radiusX != radiusY;
        var shot = BodyBuilder.MakeBlob(ref rng, 0f, 0f, rx, ry, rod ? 0.10 : 0.18, rod ? 12 : 10);
        double area = 0.0;
        for (int i = 0, n = shot.Count; i < n; i++)
        {
            var a = shot[i]; var b = shot[i + 1 == n ? 0 : i + 1];
            area += a.X * b.Y - b.X * a.Y;
        }
        return (float)(System.Math.Abs(area) * 0.5 * (material.Rho / 1000.0));
    }

    /// <summary>
    /// Injects a small fast body into a live scene, aimed from one world point at another.
    /// </summary>
    /// <remarks>
    /// <para>For the viewer: it is the difference between watching the model and poking it. The body
    /// is appended to the existing <see cref="SimState"/> and the index tables rebuilt; the solver
    /// re-derives everything else from state each tick, so nothing further is needed.</para>
    ///
    /// <para>The conservation baselines are <b>adjusted, not reset</b>. Recomputing them would zero
    /// the accumulated drift and hide exactly what the readout exists to show, so the momentum and
    /// energy the new body brings are added to the originals and the running drift stays meaningful
    /// across a whole session of firing.</para>
    ///
    /// <para>Not part of the reference scenario set and not on the fingerprint path: it draws from
    /// its own generator stream, seeded by the caller so a session can still be replayed.</para>
    /// </remarks>
    public static Result FireAt(in Result r, in SimTuning tuning, in Material material,
        float fromX, float fromY, float toX, float toY, float speed = 900f,
        float radius = 16f, float grain = 900f, int seed = 1,
        float radiusX = 0f, float radiusY = 0f, float roundMass = 0f)
    {
        SimState s = r.State;
        Solver solver = r.Solver;

        solver.TotalMomentum(out float pxBefore, out float pyBefore);
        float keBefore = solver.BodyKineticEnergy();

        var rng = new ProtoRng(seed);
        // A rod is the same round with its radii split: small face, long body. Zero means "round".
        float rx = radiusX > 0f ? radiusX : radius, ry = radiusY > 0f ? radiusY : radius;
        bool rod = radiusX > 0f && radiusY > 0f && radiusX != radiusY;
        var shot = BodyBuilder.MakeBlob(ref rng, fromX, fromY, rx, ry, rod ? 0.10 : 0.18, rod ? 12 : 10);

        // ── SIZE AND MASS ARE SEPARATE THINGS ────────────────────────────────
        // A round's mass used to be whatever its area happened to weigh, so making a round smaller
        // made it proportionally feebler and there was no way to author a small round that still
        // hit hard. Given a mass, the density is solved for it instead: rho = m / area. Measured on
        // a rock target, that holds damage flat as the round shrinks — 0.7% of the target lost at
        // radius 26, 0.7% at 13, 0.8% at 8.7 — where shrinking at fixed density drops it to 0.2%.
        //
        // This is not a pure mass change and should not be sold as one. Density also sets the
        // acoustic impedance rho*c that the carve pressure law and the contact compliance read, so
        // a small dense round is BOTH as heavy and harder-hitting per unit of face: at a quarter the
        // radius it removed 1.7%, more than the round it replaced. That is what a dense penetrator
        // does in reality, and it is the reason the lever works at all.
        Material round = material;
        if (roundMass > 0f)
        {
            double area = 0.0;
            for (int i = 0, n = shot.Count; i < n; i++)
            {
                var a = shot[i]; var b = shot[i + 1 == n ? 0 : i + 1];
                area += a.X * b.Y - b.X * a.Y;
            }
            area = System.Math.Abs(area) * 0.5;
            // MaterialProps divides Rho by 1000 before it becomes mass per unit area
            // (Materials.cs:214), so the density that produces a given mass carries that factor.
            if (area > 1e-6)
                round = new Material(material.Name, (float)(1000.0 * roundMass / area), material.C, material.Strain,
                    material.Chi, material.Crush, material.CrushRate, material.ShedLimit, material.Dent);
        }

        float dx = toX - fromX, dy = toY - fromY;
        float L = Math.SimMath.Hypot(dx, dy);
        if (L < 1e-3f) { dx = 1f; dy = 0f; L = 1f; }

        BodyBuilder.AddBody(s, ref rng, tuning, shot,
            dx / L * speed, dy / L * speed, 0f, round, grain);
        s.Reindex();
        // NOT LabelPolyEdges over every cell. AddBody has already labelled the cells it created and
        // built their touch records; relabelling from 0 resets SideTouch on every EXISTING cell, so
        // firing a shot made the whole scene read as real surface in one frame.

        solver.TotalMomentum(out float pxAfter, out float pyAfter);
        float keAfter = solver.BodyKineticEnergy();

        float mass = 0f;
        for (int b = 0; b < s.BodyCount; b++) mass += s.BodyM[b];

        return new Result(s, solver,
            r.P0x + (pxAfter - pxBefore),
            r.P0y + (pyAfter - pyBefore),
            r.Ke0 + (keAfter - keBefore),
            mass,
            Math.SimMath.Max(r.V0, speed));
    }

    /// <summary>
    /// A long rod fired at a target: the piercing round, as opposed to the blunt one.
    /// </summary>
    /// <remarks>
    /// Same machinery as <see cref="Projectile"/>; what makes it pierce is the shape and the
    /// material. The rod is elongated along its flight (<c>MakeBlob</c> takes separate radii), so it
    /// presents a small face and concentrates its pressure, and a penetrator material resists its
    /// own comminution so it stays a rod instead of mushrooming into a wide crater.
    /// </remarks>
    public static Result Pierce(in SimTuning tuning, in Material material, in Material rodMaterial,
        float speed = 1800f, float massMul = 3f, float grain = 900f, float aspect = 3.5f,
        int seed = ProtoRng.DefaultSeed)
    {
        var s = new SimState();
        var rng = new ProtoRng(seed);

        var target = BodyBuilder.MakeBlob(ref rng, 560, 350, 190, 175);
        BodyBuilder.AddBody(s, ref rng, tuning, target, 0f, 0f, 0f, material, grain);

        // Same area as the equivalent round shot, redistributed into a rod: pi r^2 = pi (r a)(r / a).
        float r = 15f * Math.SimMath.Sqrt(massMul);
        var rod = BodyBuilder.MakeBlob(ref rng, 120, 350, r * aspect, r / aspect, 0.10, 12);
        BodyBuilder.AddBody(s, ref rng, tuning, rod, speed, 0f, 0f, rodMaterial, grain);

        return Finish(s, tuning);
    }

    /// <summary>
    /// A blast: load applied to a body without anything striking it.
    /// </summary>
    /// <remarks>
    /// <para>What separates a grenade from a bullet is not the amount of energy but how it arrives.
    /// A bullet is one cell pushing on one cell; a blast is a pressure front arriving at the whole
    /// exposed face at once. So there is no impactor body — the impulse is applied directly to the
    /// deviation velocity of every cell with free surface, and <c>DecomposeMotion</c> promotes the
    /// rigid part to the body in the same substep, exactly as a contact impulse would. Cells in the
    /// interior feel it through their bonds a moment later, which is the shock travelling in.</para>
    ///
    /// <para>Only cells with a free edge are loaded. That is the cheap stand-in for line of sight —
    /// a buried cell has no exposed face for a pressure front to push on — and it costs one call to
    /// <see cref="Solver.HasFreeEdge"/> rather than a raycast per cell.</para>
    ///
    /// <para>The load is a PRESSURE on the exposed face: impulse is <c>p · L · falloff</c>, so a cell
    /// accelerates by <c>p·L/m</c> — small cells fly and dense ones resist, which is what a front
    /// does. The falloff is <c>(1 − d/radius)²</c> rather than <c>1/r²</c>: the inverse square has no
    /// bound at the centre and is already negligible a few cells out, which made the first version
    /// deliver 0.15 px/s at 50 px and do visibly nothing.</para>
    ///
    /// <para>The impulse is injected from nothing, which is honest — an explosion carries its own
    /// momentum — so the scene's reference momentum and energy move with it, the way
    /// <see cref="FireAt"/> already does for a fired round. Without that every conservation check
    /// would read the blast as a break.</para>
    /// </remarks>
    /// <param name="pressure">Peak impulse per unit of exposed length, at the blast centre.</param>
    /// <param name="radius">Beyond this the front has nothing left to give.</param>
    public static Result Blast(in Result r, in SimTuning tuning,
        float fromX, float fromY, float pressure = 3e5f, float radius = 260f)
    {
        SimState s = r.State;
        Solver solver = r.Solver;

        solver.TotalMomentum(out float pxBefore, out float pyBefore);
        float keBefore = solver.BodyKineticEnergy();

        for (int c = 0; c < s.CellCount; c++)
        {
            if (s.Dead(c)) continue;
            int b = s.CellBody[c];
            if (b < 0 || b >= s.BodyCount) continue;
            if (!solver.HasFreeEdge(c)) continue;               // nothing for a front to push on

            Math.SimMath.SinCos(s.BodyRot[b], out float si, out float co);
            float wx = s.BodyX[b] + s.CellRx[c] * co - s.CellRy[c] * si;
            float wy = s.BodyY[b] + s.CellRx[c] * si + s.CellRy[c] * co;

            float dx = wx - fromX, dy = wy - fromY;
            float d = Math.SimMath.Hypot(dx, dy);
            if (d >= radius || d < 1e-3f) continue;

            // Scaled by the cell's own exposed length, so a big face takes more of the front than a
            // small one, and falling to nothing at the radius.
            float fall = 1f - d / radius;
            float face = Math.SimMath.Max(1f, s.CellPerim[c] * 0.25f);
            float j = pressure * face * fall * fall;
            float ax = j * (dx / d) / Math.SimMath.Max(1e-6f, s.CellM[c]);
            float ay = j * (dy / d) / Math.SimMath.Max(1e-6f, s.CellM[c]);

            s.CellDvx[c] += ax * co + ay * si;                  // body-local, as the solver carries it
            s.CellDvy[c] += -ax * si + ay * co;
        }

        solver.TotalMomentum(out float pxAfter, out float pyAfter);
        float keAfter = solver.BodyKineticEnergy();

        float mass = 0f;
        for (int b = 0; b < s.BodyCount; b++) mass += s.BodyM[b];

        return new Result(s, solver,
            r.P0x + (pxAfter - pxBefore),
            r.P0y + (pyAfter - pyBefore),
            r.Ke0 + Math.SimMath.Max(0f, keAfter - keBefore),
            mass,
            r.V0);
    }

    // ── the reference scene set ──────────────────────────────────────────────

    /// <summary>
    /// The scenes the correctness tests and the bench report run, by name. One list, so the two can
    /// never disagree about what "the collide scene" is.
    /// </summary>
    public static readonly string[] ReferenceNames =
    {
        "collide", "collide-glass", "projectile", "spin", "steel-projectile", "glass-projectile", "shell",
    };

    /// <summary>
    /// The finest grain every listed material may be built at under <paramref name="t"/>: the
    /// largest of their <see cref="BodyBuilder.MinGrain"/> floors.
    /// </summary>
    public static float FloorGrain(in SimTuning t, params Material[] materials)
    {
        float g = 0f;
        foreach (var m in materials) g = Math.SimMath.Max(g, BodyBuilder.MinGrain(m, t));
        return g;
    }

    /// <summary>
    /// Builds reference scene <paramref name="name"/> with every body at the floor grain of its
    /// materials — the densest mesh the builder will produce at the configured substep count.
    /// </summary>
    public static Result Reference(string name, SimConfig cfg)
    {
        SimTuning t = cfg.Tuning;
        Material rock = cfg.Material("rock"), glass = cfg.Material("glass"), steel = cfg.Material("steel");
        return name switch
        {
            "collide" => Collide(t, rock, 600f, FloorGrain(t, rock)),
            "collide-glass" => Collide(t, glass, 600f, FloorGrain(t, glass)),
            "projectile" => Projectile(t, rock, 900f, 3f, FloorGrain(t, rock)),
            "spin" => Spin(t, rock, grain: FloorGrain(t, rock)),
            "steel-projectile" => Projectile(t, steel, 1500f, 8f, FloorGrain(t, steel)),
            "glass-projectile" => Projectile(t, glass, 1500f, 8f, FloorGrain(t, glass)),
            "shell" => Shell(t, steel, rock, grain: FloorGrain(t, steel, rock)),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a reference scene"),
        };
    }

    private static Result Finish(SimState s, in SimTuning tuning)
    {
        var solver = new Solver(s, tuning);
        solver.TotalMomentum(out float px, out float py);
        float mass = 0f;
        for (int b = 0; b < s.BodyCount; b++) mass += s.BodyM[b];
        float v0 = solver.MaxMaterialSpeed();
        if (v0 < 1f) v0 = 1f;
        float ke0 = solver.BodyKineticEnergy();
        if (ke0 < 1e-9f) ke0 = 1e-9f;
        return new Result(s, solver, px, py, ke0, mass, v0);
    }
}
