using AsteroidsEngine.Engine.Rendering;

namespace AsteroidsEngine.Engine.Core;

public interface ISystem
{
    void Update(World world, double dt);
}

/// <summary>
/// Systems that need to draw implement this in addition to ISystem.
/// Called after all ISystem.Update calls complete, with the active renderer.
/// </summary>
public interface IDrawSystem
{
    void Draw(World world, IRenderer renderer);
}
