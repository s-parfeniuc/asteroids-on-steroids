using System.Numerics;
using AsteroidsEngine.Engine.Collision;
using AsteroidsEngine.Engine.Core;

namespace AsteroidsEngine.Engine.Events;

public record CollisionEvent(Entity EntityA, Entity EntityB, ContactInfo Contact);
