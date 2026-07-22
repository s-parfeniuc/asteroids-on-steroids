namespace AsteroidsEngine.Engine.Components;

// Zero-data marker components used as query filters.
// Add with: world.AddComponent(entity, new DestroyTag());

/// <summary>Entity will be removed by World.FlushDeferred() this frame.</summary>
public struct DestroyTag { }

/// <summary>Entity exists but is skipped by all systems (paused, pooled, etc.).</summary>
public struct DisabledTag { }
