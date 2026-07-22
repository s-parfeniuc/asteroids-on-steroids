namespace AsteroidsEngine.Engine.Rendering;

/// <summary>
/// Engine-side RGBA colour (8-bit channels). Backend-agnostic: replaces use of
/// System.Drawing.Color / SkiaSharp.SKColor across the engine and games so that
/// no rendering code depends on a UI toolkit. Each platform backend converts this
/// to its native colour type at the draw boundary.
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    public readonly byte R, G, B, A;

    public Color(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

    public static Color FromRgba(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);

    /// <summary>Returns this colour with a replaced alpha channel.</summary>
    public Color WithAlpha(byte a) => new(R, G, B, a);

    /// <summary>Linear interpolation between two colours (incl. alpha); t clamped to [0,1].</summary>
    public static Color Lerp(Color a, Color b, float t)
    {
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return new Color(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }

    public static readonly Color White       = new(255, 255, 255);
    public static readonly Color Black        = new(0, 0, 0);
    public static readonly Color Transparent  = new(0, 0, 0, 0);

    public bool Equals(Color o) => R == o.R && G == o.G && B == o.B && A == o.A;
    public override bool Equals(object? o) => o is Color c && Equals(c);
    public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
    public static bool operator ==(Color a, Color b) => a.Equals(b);
    public static bool operator !=(Color a, Color b) => !a.Equals(b);
}
