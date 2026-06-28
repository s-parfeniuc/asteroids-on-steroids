namespace AsteroidsGame.Components;

public struct PlayerTag      { }
public struct AsteroidTag    { }
public struct AlienTag       { }
public struct BulletTag      { }
/// <summary>Marker on bullets fired by alien ships so they can target the player layer.</summary>
public struct AlienBulletTag { }

/// <summary>Key into GameConfig.Asteroids (e.g. "standard", "boulder").</summary>
public struct AsteroidVariant { public string Key; }

/// <summary>Key into GameConfig.Entities (e.g. "drone", "bruiser", "mothership").</summary>
public struct AlienVariant { public string Key; }

/// <summary>Tracks which GameConfig asteroid type key spawned this body, enabling live
/// material sync: editor changes to that material propagate to the running simulation.
/// (Used by the demo/editor; harmless in the game.)</summary>
public struct AsteroidTypeKey { public string Key; }
