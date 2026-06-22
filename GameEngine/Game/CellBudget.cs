namespace AsteroidsGame;

/// <summary>
/// Global tracker for the total number of live cells across all entities.
/// Incremented when an asteroid/alien spawns (by its cell count), decremented
/// when a cell vaporises. The spawner checks CanSpawn before placing a new body.
/// </summary>
public sealed class CellBudget
{
    private int _count;

    public int Count => _count;

    public void Add(int cellCount)          => _count += cellCount;
    public void Remove(int cellCount)       => _count = Math.Max(0, _count - cellCount);
    public bool CanSpawn(int cellCount, int max) => _count + cellCount <= max;

    public void Reset() => _count = 0;
}
