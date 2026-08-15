using System;

namespace AsteroidsSim.Math;

/// <summary>
/// The simulation's 2D vector.
/// </summary>
/// <remarks>
/// <para>Deliberately not <c>System.Numerics.Vector2</c> and deliberately not <c>Godot.Vector2</c>:
/// the simulation must not depend on the engine's types (PORT_PLAN.md §2.2), and
/// <c>System.Numerics.Vector2</c> is SIMD-accelerated in ways whose exact behaviour we would rather not
/// have to reason about inside the determinism contract.</para>
///
/// <para>Every operation here is plain scalar <c>+ - * /</c>, which IEEE 754 specifies exactly.</para>
/// </remarks>
public readonly struct Vec2 : IEquatable<Vec2>
{
    public readonly float X, Y;

    public Vec2(float x, float y) { X = x; Y = y; }

    public static Vec2 Zero => new(0f, 0f);
    public static Vec2 UnitX => new(1f, 0f);
    public static Vec2 UnitY => new(0f, 1f);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(float s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, float s) => new(a.X / s, a.Y / s);

    public float LengthSquared => X * X + Y * Y;
    public float Length => SimMath.Sqrt(X * X + Y * Y);

    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>2D cross product (the z component of the 3D cross).</summary>
    public static float Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Unit vector, or <see cref="Zero"/> if the length is below <paramref name="epsilon"/>.</summary>
    public Vec2 Normalized(float epsilon = 1e-6f)
    {
        float len = Length;
        return len > epsilon ? new Vec2(X / len, Y / len) : Zero;
    }

    /// <summary>Rotate by <paramref name="radians"/>.</summary>
    public Vec2 Rotated(float radians)
    {
        SimMath.SinCos(radians, out float s, out float c);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }

    /// <summary>Perpendicular, rotated +90 degrees.</summary>
    public Vec2 Perpendicular => new(-Y, X);

    public bool Equals(Vec2 o) => X == o.X && Y == o.Y;
    public override bool Equals(object? o) => o is Vec2 v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Vec2 a, Vec2 b) => a.Equals(b);
    public static bool operator !=(Vec2 a, Vec2 b) => !a.Equals(b);
    public override string ToString() => $"({X:R}, {Y:R})";
}
