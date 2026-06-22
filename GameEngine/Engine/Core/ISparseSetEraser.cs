namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// Non-generic interface so World can call Remove on a SparseSet&lt;T&gt;
/// without knowing T at the call site (needed for DestroyImmediate).
/// </summary>
internal interface ISparseSetEraser
{
    void RemoveById(int entityId);
}
