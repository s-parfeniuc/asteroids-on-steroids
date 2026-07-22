namespace AsteroidsEngine.Engine.Components;

/// <summary>
/// Per-entity multipliers for forces applied by VortexSystem.
/// Only entities that have this component are affected by the vortex.
/// Negative values make the entity resist or oppose the vortex direction.
/// </summary>
public struct VortexResponse
{
    /// <summary>Scales the centripetal (inward) vortex force. &lt;0 = pushed outward.</summary>
    public float CentripetalMult;
    /// <summary>Scales the tangential (CCW) vortex force. &lt;0 = reversed spin direction.</summary>
    public float TangentialMult;
}
