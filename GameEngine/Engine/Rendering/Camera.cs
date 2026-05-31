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

    public Camera(int screenWidth, int screenHeight)
    {
        ScreenWidth  = screenWidth;
        ScreenHeight = screenHeight;
    }

    /// <summary>
    /// World→screen transform. Push onto the renderer before drawing world-space
    /// entities; pop before drawing screen-space HUD. (Row-vector convention:
    /// screen = world * GetViewMatrix().)
    /// </summary>
    public Matrix3x2 GetViewMatrix()
        => Matrix3x2.CreateTranslation(-Position)
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
