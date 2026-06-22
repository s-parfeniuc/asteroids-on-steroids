namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// Typed sparse set for one component type T.
///
/// Layout:
///   sparse[entityId]  → index into dense/data, or -1 if absent
///   dense[i]          → entity ID of the i-th stored component
///   data[i]           → component value for dense[i]
///
/// All three arrays are kept in sync. Removal swaps the target slot
/// with the last slot, then decrements Count — O(1) with no gaps.
/// </summary>
internal sealed class SparseSet<T> : ISparseSetEraser where T : struct
{
    private const int InitialCapacity = 64;

    // Indexed by entity ID. Value = index into dense[], or -1.
    private int[] _sparse;

    // Packed entity IDs and their component values.
    private int[] _dense;
    private T[]   _data;

    public int Count { get; private set; }

    public SparseSet(int maxEntities)
    {
        _sparse = new int[maxEntities];
        Array.Fill(_sparse, -1);

        _dense = new int[InitialCapacity];
        _data  = new T[InitialCapacity];
    }

    public bool Has(int entityId) =>
        entityId < _sparse.Length && _sparse[entityId] >= 0;

    public int GetDenseIndex(int entityId) => _sparse[entityId];

    public ref T GetByDenseIndex(int denseIndex) => ref _data[denseIndex];

    void ISparseSetEraser.RemoveById(int entityId) => Remove(entityId);

    public ref T Get(int entityId)
    {
        int idx = _sparse[entityId];
        if (idx < 0)
            throw new InvalidOperationException(
                $"Entity {entityId} does not have component {typeof(T).Name}.");
        return ref _data[idx];
    }

    public void Set(int entityId, T value)
    {
        if (entityId >= _sparse.Length)
            GrowSparse(entityId + 1);

        if (_sparse[entityId] >= 0)
        {
            // Already exists — update in place.
            _data[_sparse[entityId]] = value;
            return;
        }

        if (Count == _dense.Length)
            GrowDense();

        _sparse[entityId] = Count;
        _dense[Count]     = entityId;
        _data[Count]      = value;
        Count++;
    }

    public bool Remove(int entityId)
    {
        if (entityId >= _sparse.Length || _sparse[entityId] < 0)
            return false;

        int removedIdx = _sparse[entityId];
        int lastIdx    = Count - 1;

        if (removedIdx != lastIdx)
        {
            // Move last element into the removed slot.
            int lastEntityId      = _dense[lastIdx];
            _dense[removedIdx]    = lastEntityId;
            _data[removedIdx]     = _data[lastIdx];
            _sparse[lastEntityId] = removedIdx;
        }

        _sparse[entityId] = -1;
        Count--;
        return true;
    }

    /// <summary>
    /// Returns all (entityId, component) pairs currently stored.
    /// Safe to read; do not add/remove during iteration.
    /// </summary>
    public ReadOnlySpan<int> DenseIds  => _dense.AsSpan(0, Count);
    public ReadOnlySpan<T>   DenseData => _data.AsSpan(0, Count);

    /// <summary>
    /// Raw backing array for parallel iteration. Valid indices are [0, Count).
    /// Never resize or write outside [0, Count) while a parallel loop is running.
    /// </summary>
    internal int[] RawDenseIds => _dense;

    private void GrowSparse(int minLength)
    {
        int newLength = Math.Max(_sparse.Length * 2, minLength);
        var next = new int[newLength];
        Array.Fill(next, -1);
        _sparse.CopyTo(next, 0);
        _sparse = next;
    }

    private void GrowDense()
    {
        int newCap = _dense.Length * 2;
        Array.Resize(ref _dense, newCap);
        Array.Resize(ref _data,  newCap);
    }
}
