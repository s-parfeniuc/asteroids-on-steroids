namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// An entity is just an ID. The version guards against stale references:
/// if entity {Id=5, Version=1} is destroyed and ID 5 is recycled,
/// the new entity gets {Id=5, Version=2}, making the old handle invalid.
/// </summary>
public readonly struct Entity : IEquatable<Entity>
{
    public readonly int Id;
    public readonly int Version;

    public bool IsNull => Id == 0;
    public static readonly Entity Null = new(0, 0);

    internal Entity(int id, int version)
    {
        Id      = id;
        Version = version;
    }

    public bool Equals(Entity other) => Id == other.Id && Version == other.Version;
    public override bool Equals(object? obj) => obj is Entity e && Equals(e);
    public override int GetHashCode() => HashCode.Combine(Id, Version);
    public override string ToString() => $"Entity({Id}:{Version})";

    public static bool operator ==(Entity a, Entity b) => a.Equals(b);
    public static bool operator !=(Entity a, Entity b) => !a.Equals(b);
}
