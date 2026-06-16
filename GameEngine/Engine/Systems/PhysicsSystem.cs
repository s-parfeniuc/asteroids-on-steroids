using System.Numerics;
using AsteroidsEngine.Engine.Components;
using AsteroidsEngine.Engine.Core;
using AsteroidsEngine.Engine.Diagnostics;

namespace AsteroidsEngine.Engine.Systems;

/// <summary>
/// Applies forces and drag to entities with Velocity + RigidBody.
/// Uses symplectic Euler: velocity is updated before position
/// (MovementSystem runs after this and reads the updated velocity).
///
/// Update order per frame:
///   PhysicsSystem (apply forces → update velocity)
///   MovementSystem (position += velocity * dt)
///   CollisionSystem (detect + resolve)
/// </summary>
public sealed class PhysicsSystem : ISystem
{
    public Vector2 Gravity { get; set; } = Vector2.Zero;

    public void Update(World world, double dt)
    {
        float fdt = (float)dt;

        world.ForEach<Velocity, RigidBody>((Entity e, ref Velocity v, ref RigidBody rb) =>
        {
            if (rb.Mass <= 0f) return;
            if (rb.Asleep) return;   // resting body — skip integration until woken

            // Accumulate gravity as a force.
            rb.AccumulatedForce += Gravity * rb.Mass;

            // Symplectic Euler: update velocity first.
            Vector2 dvLin = (rb.AccumulatedForce / rb.Mass) * fdt;
            float dvAng = rb.Inertia > 0f ? (rb.AccumulatedTorque / rb.Inertia) * fdt : 0f;
            if (ForceLog.On(ForceCat.Integration, e.Id) &&
                (rb.AccumulatedForce != Vector2.Zero || rb.AccumulatedTorque != 0f))
                ForceLog.Write(ForceCat.Integration, e.Id,
                    $"v += (F{ForceLog.V(rb.AccumulatedForce)}/m{rb.Mass:0.#})·dt{fdt:0.####} = {ForceLog.V(dvLin)} ; " +
                    $"ω += (τ{rb.AccumulatedTorque:0.#}/I{rb.Inertia:0.#})·dt = {dvAng:0.####}");
            v.Linear  += dvLin;
            v.Angular += dvAng;

            // Apply drag: velocity *= e^(-drag * dt). Decay rate (s⁻¹); 0 = none.
            float linearRetain  = MathF.Exp(-rb.LinearDrag  * fdt);
            float angularRetain = MathF.Exp(-rb.AngularDrag * fdt);
            if (ForceLog.On(ForceCat.Drag, e.Id) && (rb.LinearDrag > 0f || rb.AngularDrag > 0f))
                ForceLog.Write(ForceCat.Drag, e.Id,
                    $"v {ForceLog.V(v.Linear)} *= e^(-drag{rb.LinearDrag:0.##}·dt) {linearRetain:0.####} ; " +
                    $"ω {v.Angular:0.##} *= {angularRetain:0.####}");
            v.Linear  *= linearRetain;
            v.Angular *= angularRetain;

            // Reset forces for next frame.
            rb.AccumulatedForce  = Vector2.Zero;
            rb.AccumulatedTorque = 0f;
        });
    }

    /// <summary>
    /// Apply an instantaneous force impulse to an entity (e.g. thrust, explosion).
    /// Safe to call from any system before PhysicsSystem runs.
    /// </summary>
    public static void ApplyForce(World world, Entity entity, Vector2 force)
    {
        if (!world.HasComponent<RigidBody>(entity)) return;
        ref var rb = ref world.GetComponent<RigidBody>(entity);
        rb.Asleep = false; rb.SleepTimer = 0f;   // applying force wakes the body
        rb.AccumulatedForce += force;
        if (ForceLog.On(ForceCat.Thrust, entity.Id))
            ForceLog.Write(ForceCat.Thrust, entity.Id,
                $"applied force {ForceLog.V(force)} → Δv this step ≈ {ForceLog.V(force / rb.Mass * (1f / 120f))} (F/m·dt)");
    }

    /// <summary>
    /// Apply an off-centre force impulse, contributing both linear force and torque.
    /// contactOffset = world contact point − entity centroid (world space).
    /// </summary>
    public static void ApplyForceAtPoint(World world, Entity entity,
                                          Vector2 force, Vector2 contactOffset)
    {
        if (!world.HasComponent<RigidBody>(entity)) return;
        ref var rb = ref world.GetComponent<RigidBody>(entity);
        rb.Asleep = false; rb.SleepTimer = 0f;   // applying force wakes the body
        rb.AccumulatedForce  += force;
        // 2-D cross product: torque = r × F = rx*Fy − ry*Fx
        rb.AccumulatedTorque += contactOffset.X * force.Y - contactOffset.Y * force.X;
    }
}
