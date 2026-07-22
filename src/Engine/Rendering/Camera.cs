using System;
using System.Numerics;

namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Viewport into the game world. Produces a world→screen transform as a
/// <see cref="Matrix3x2"/> (UI-toolkit-free) that render systems push onto an
/// <see cref="IRenderer"/> before drawing world-space entities. Screen-space HUD
/// draws without it.
///
///   world → screen:  translate world so the camera maps to the screen centre,
///                     then scale by Zoom.
/// </summary>
public sealed class Camera
{
    public Vector2 Position { get; set; } = Vector2.Zero;
    public float   Zoom     { get; set; } = 1f;

    public int ScreenWidth  { get; set; }
    public int ScreenHeight { get; set; }

    // ── Screen shake (trauma model: shake ∝ trauma², decays over time) ─────────
    /// <summary>Current render offset from shake. Applied to the view only, not to picking.</summary>
    public Vector2 ShakeOffset { get; private set; } = Vector2.Zero;
    /// <summary>Settings multiplier; 0 disables shake entirely.</summary>
    public float   ShakeIntensity { get; set; } = 1f;
    /// <summary>Peak shake displacement (px) at full trauma.</summary>
    public float   MaxShakePixels { get; set; } = 26f;

    private float _trauma;
    private readonly Random _shakeRng = new();
    private const float TraumaDecay = 1.8f;   // per second

    public Camera(int screenWidth, int screenHeight)
    {
        ScreenWidth  = screenWidth;
        ScreenHeight = screenHeight;
    }

    /// <summary>Adds shake energy (0..1). Impacts/kills call this; larger events add more.</summary>
    public void AddTrauma(float amount) => _trauma = Math.Clamp(_trauma + amount, 0f, 1f);

    /// <summary>Decays trauma and recomputes the per-frame shake offset. Call once per rendered frame.</summary>
    public void UpdateShake(float dt)
    {
        if (_trauma <= 0f || ShakeIntensity <= 0f) { ShakeOffset = Vector2.Zero; return; }
        _trauma = MathF.Max(0f, _trauma - dt * TraumaDecay);
        float mag = MaxShakePixels * ShakeIntensity * _trauma * _trauma;
        float ang = (float)(_shakeRng.NextDouble() * MathF.Tau);
        float r   = mag * (float)_shakeRng.NextDouble();
        ShakeOffset = new Vector2(MathF.Cos(ang) * r, MathF.Sin(ang) * r);
    }

    /// <summary>
    /// World→screen transform. Push onto the renderer before drawing world-space
    /// entities; pop before drawing screen-space HUD. (Row-vector convention:
    /// screen = world * GetViewMatrix().)
    /// </summary>
    public Matrix3x2 GetViewMatrix()
        => Matrix3x2.CreateTranslation(-(Position + ShakeOffset))
         * Matrix3x2.CreateScale(Zoom)
         * Matrix3x2.CreateTranslation(ScreenWidth / 2f, ScreenHeight / 2f);

    /// <summary>Converts a screen pixel position to world coordinates (mouse picking).</summary>
    public Vector2 ScreenToWorld(Vector2 screen)
        => new((screen.X - ScreenWidth  / 2f) / Zoom + Position.X,
               (screen.Y - ScreenHeight / 2f) / Zoom + Position.Y);

    /// <summary>Converts a world position to screen pixel coordinates.</summary>
    public Vector2 WorldToScreen(Vector2 world)
        => new((world.X - Position.X) * Zoom + ScreenWidth  / 2f,
               (world.Y - Position.Y) * Zoom + ScreenHeight / 2f);
}
