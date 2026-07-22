using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Events;

/// <summary><paramref name="ApproachSpeed"/> is the PRE-solve normal closing speed at the deepest
/// contact (≥0; 0 if separating), captured before the impulse solve so fracture handlers compute
/// impact energy from the true approach velocity rather than the post-bounce one.</summary>
public record CollisionEvent(Entity EntityA, Entity EntityB, ContactInfo Contact, float ApproachSpeed = 0f);
