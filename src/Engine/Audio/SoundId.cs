namespace AsteroidsEngine.Engine.Resources;

/// <summary>Opaque handle to a backend-loaded sound. Default value is Invalid. Consumed by the
/// (currently stub) IAudioBackend; kept for the deferred audio sprint. No image/texture handle —
/// the game has no textures.</summary>
public readonly struct SoundId : IEquatable<SoundId>
{
    public readonly int Value;
    public SoundId(int value) => Value = value;

    public bool IsValid => Value > 0;
    public static readonly SoundId Invalid = default;

    public bool Equals(SoundId o) => Value == o.Value;
    public override bool Equals(object? o) => o is SoundId s && Equals(s);
    public override int GetHashCode() => Value;
}
