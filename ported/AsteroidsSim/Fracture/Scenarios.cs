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
