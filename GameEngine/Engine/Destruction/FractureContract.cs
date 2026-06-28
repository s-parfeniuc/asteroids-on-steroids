using System.Numerics;

namespace AsteroidsEngine.Engine.Destruction;

/// <summary>
/// Inputs to one fracture evaluation. The FractureSystem computes the energy terms
/// from the collision/velocities; the simulator does the geometry + physics. Angles
/// in radians; directions are unit vectors in world space.
/// </summary>
public struct FractureInput
{
    public Vector2 ImpactPointWorld;  // exact surface hit point (fragments fling away from it)
    public Vector2 ImpactDir;         // shot direction (steers the directional crack cone)
    public float   Directionality;    // effective (weapon + material)/2: 0 = splash … 1 = forward channel
    public float   BlastFraction;     // weapon: carves the vaporize budget from each cell's energy

    public Vector2 BodyPosition;      // Transform.Position (world centroid)
    public float   BodyRotation;
    public Vector2 BodyLinear;
    public float   BodyAngular;       // body spin ω (spin pre-stress on rim bonds)
    public float   BodyMass;          // current RigidBody.Mass
}

/// <summary>One resulting body from a fracture: a connected component of cells.</summary>
public struct FragmentSpec
{
    public FracturableBody Body;       // re-centred cells + internal bonds, ready to spawn
    public Vector2 WorldCentroid;      // Transform.Position for the new entity
    public float   Rotation;           // Transform.Rotation (= parent's; cells are parent-local un-rotated)
    public Vector2 Linear;
    public float   Angular;
    public float   Mass;
    public float   Inertia;
    public float   Area;
    public bool    IsDebris;           // single tiny cell → visual particle, not a physics body
}

/// <summary>
/// Output of a one-shot fracture (the multi-frame path uses events instead).
/// </summary>
public struct FractureResult
{
    public bool           Fractured;
    public FragmentSpec[] Fragments;
    public Vector2        ImpactPointWorld;
}
