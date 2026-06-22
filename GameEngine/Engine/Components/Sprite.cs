using System.Numerics;
using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Visual representation of an entity. Consumed by RenderSystem.
/// ImageId is a key into ResourceManager. If empty, nothing is drawn
/// (useful for invisible entities like triggers).
/// </summary>
public struct Sprite
{
    public string  ImageId;  // ResourceManager key; empty = invisible
    public int     Layer;    // draw order; lower = drawn first (behind)
    public Vector2 Offset;   // draw offset from Transform.Position (in world units)
    public Color   Tint;     // color multiplied over the image; White = no tint

    public static Sprite Create(string imageId, int layer = 0) =>
        new() { ImageId = imageId, Layer = layer, Tint = Color.White };
}
