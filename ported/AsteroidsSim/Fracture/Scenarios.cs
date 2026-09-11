using System;
using System.Collections.Generic;

namespace AsteroidsSim.Fracture;

/// <summary>
/// The reference scenarios from the prototype, used for equivalence testing, the determinism
/// fingerprint and calibration sweeps. Not gameplay content.
/// </summary>
/// <remarks>
/// Body creation order is part of the determinism contract — all bodies in a scene draw from one
/// generator stream, so the order in which they are built decides what every one of them looks
/// like. These builders therefore construct in exactly the reference's order.
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
        float radius = 16f, float grain = 900f, int seed = 1)
    {
        SimState s = r.State;
        Solver solver = r.Solver;

        solver.TotalMomentum(out float pxBefore, out float pyBefore);
        float keBefore = solver.BodyKineticEnergy();

        var rng = new ProtoRng(seed);
        var shot = BodyBuilder.MakeBlob(ref rng, fromX, fromY, radius, radius, 0.18, 10);

        float dx = toX - fromX, dy = toY - fromY;
        float L = Math.SimMath.Hypot(dx, dy);
        if (L < 1e-3f) { dx = 1f; dy = 0f; L = 1f; }

        BodyBuilder.AddBody(s, ref rng, tuning, shot,
            dx / L * speed, dy / L * speed, 0f, material, grain);
        s.Reindex();

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
