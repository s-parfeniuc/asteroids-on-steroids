namespace AsteroidsEngine.Engine.Resources;

/// <summary>Opaque handle to a backend-loaded image. Default value is Invalid.</summary>
public readonly struct ImageId : IEquatable<ImageId>
{
    public readonly int Value;
    public ImageId(int value) => Value = value;

    public bool IsValid => Value > 0;
    public static readonly ImageId Invalid = default;

    public bool Equals(ImageId o) => Value == o.Value;
    public override bool Equals(object? o) => o is ImageId i && Equals(i);
    public override int GetHashCode() => Value;
}

/// <summary>Opaque handle to a backend-loaded sound. Default value is Invalid.</summary>
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
