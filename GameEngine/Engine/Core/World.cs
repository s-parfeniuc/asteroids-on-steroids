namespace AsteroidsEngine.Engine.Core;

/// <summary>
/// The ECS world. Owns all entity IDs and component data.
///
/// Entity lifecycle
///   CreateEntity()         → allocate an ID (reuse recycled ones first)
///   DestroyEntity(e)       → deferred; actually removed on FlushDeferred()
///   DestroyImmediate(e)    → removes at once (use only outside update loops)
///
/// Component access
///   AddComponent / SetComponent  → attach or overwrite a component
///   GetComponent<T>(e)           → ref to the live value (mutate in place)
///   TryGetComponent<T>(e)        → out ref, returns false if absent
///   HasComponent<T>(e)           → existence check
///   RemoveComponent<T>(e)        → deferred removal
///
/// Queries
///   Query<T>()       → all entities that have T
///   Query<T1,T2>()   → all entities that have both T1 and T2
///   (see QueryResult helpers below for ref-based mutation)
/// </summary>
public sealed class World
{
    private const int DefaultMaxEntities = 4096;

    // --- Entity ID pool ---
    private int _nextId = 1;                   // 0 is reserved for Entity.Null
    private int[] _versions;                     // version[id] increments on recycle
    private readonly Queue<int> _recycled = new();

    // --- Component storage ---
    // One SparseSet<T> per component type, keyed by Type.
    private readonly Dictionary<Type, object> _stores = new();

    // --- Deferred operations ---
    private readonly List<Entity> _pendingDestroy = new();
    private readonly List<(Type, int)> _pendingRemoveComp = new();

    public World(int maxEntities = DefaultMaxEntities)
    {
        _versions = new int[maxEntities];
    }

    // -------------------------------------------------------------------------
    // Entity lifecycle
    // -------------------------------------------------------------------------

    public Entity CreateEntity()
    {
        int id;
        if (_recycled.Count > 0)
        {
            id = _recycled.Dequeue();
        }
        else
        {
            id = _nextId++;
            if (id >= _versions.Length)
                Array.Resize(ref _versions, _versions.Length * 2);
        }

        return new Entity(id, _versions[id]);
    }

    /// <summary>Marks entity for removal. Actual removal happens in FlushDeferred().</summary>
    public void DestroyEntity(Entity e)
    {
        if (!IsAlive(e)) return;
        _pendingDestroy.Add(e);
    }

    /// <summary>Removes entity and all its components immediately. Safe outside update loops.</summary>
    public void DestroyImmediate(Entity e)
    {
        if (!IsAlive(e)) return;
        foreach (var store in _stores.Values)
            ((ISparseSetEraser)store).RemoveById(e.Id);
        _versions[e.Id]++;
        _recycled.Enqueue(e.Id);
    }

    public bool IsAlive(Entity e) =>
        !e.IsNull && e.Id < _versions.Length && _versions[e.Id] == e.Version;

    // -------------------------------------------------------------------------
    // Component access
    // -------------------------------------------------------------------------

    public void AddComponent<T>(Entity e, T component) where T : struct
    {
        AssertAlive(e);
        Store<T>().Set(e.Id, component);
    }

    public ref T GetComponent<T>(Entity e) where T : struct
    {
        AssertAlive(e);
        return ref Store<T>().Get(e.Id);
    }

    /// <summary>
    /// Returns false (and default) if the entity is dead or lacks the component.
    /// Note: the returned value is a copy. To mutate, use GetComponent() instead.
    /// </summary>
    public bool TryGetComponent<T>(Entity e, out T value) where T : struct
    {
        value = default;
        if (!IsAlive(e)) return false;
        var store = Store<T>();
        if (!store.Has(e.Id)) return false;
        value = store.Get(e.Id);
        return true;
    }

    public bool HasComponent<T>(Entity e) where T : struct =>
        IsAlive(e) && Store<T>().Has(e.Id);

    /// <summary>Defers removal to FlushDeferred(). Safe to call during iteration.</summary>
    public void RemoveComponent<T>(Entity e) where T : struct
    {
        if (IsAlive(e)) _pendingRemoveComp.Add((typeof(T), e.Id));
    }

    /// <summary>Removes component immediately. Safe outside update loops.</summary>
    public void RemoveComponentImmediate<T>(Entity e) where T : struct
    {
        if (IsAlive(e)) Store<T>().Remove(e.Id);
    }

    // -------------------------------------------------------------------------
    // ForEach — iterate with ref access to component data
    // -------------------------------------------------------------------------

    /// <summary>Calls action for every entity that has T.</summary>
    public void ForEach<T>(EcsAction<T> action) where T : struct
    {
        var store = Store<T>();
        int count = store.Count;
        for (int i = 0; i < count; i++)
        {
            int id = store.DenseIds[i];
            action(new Entity(id, _versions[id]), ref store.GetByDenseIndex(i));
        }
    }

    /// <summary>Calls action for every entity that has both T1 and T2.</summary>
    public void ForEach<T1, T2>(EcsAction<T1, T2> action)
        where T1 : struct
        where T2 : struct
    {
        var s1 = Store<T1>();
        var s2 = Store<T2>();
        int count = s1.Count;
        for (int i = 0; i < count; i++)
        {
            int id = s1.DenseIds[i];
            if (!s2.Has(id)) continue;
            action(
                new Entity(id, _versions[id]),
                ref s1.GetByDenseIndex(i),
                ref s2.GetByDenseIndex(s2.GetDenseIndex(id)));
        }
    }

    /// <summary>Calls action for every entity that has T1, T2, and T3.</summary>
    public void ForEach<T1, T2, T3>(EcsAction<T1, T2, T3> action)
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        var s1 = Store<T1>();
        var s2 = Store<T2>();
        var s3 = Store<T3>();
        int count = s1.Count;
        for (int i = 0; i < count; i++)
        {
            int id = s1.DenseIds[i];
            if (!s2.Has(id) || !s3.Has(id)) continue;
            action(
                new Entity(id, _versions[id]),
                ref s1.GetByDenseIndex(i),
                ref s2.GetByDenseIndex(s2.GetDenseIndex(id)),
                ref s3.GetByDenseIndex(s3.GetDenseIndex(id)));
        }
    }

    // -------------------------------------------------------------------------
    // ForEachParallel — same signatures as ForEach; bodies run on ThreadPool.
    //
    // Preconditions (caller's responsibility — not enforced at runtime):
    //   • Body reads/writes only its own entity's components.
    //   • No CreateEntity / DestroyEntity inside the body.
    //   • No unguarded shared mutable state (use Interlocked or thread-locals).
    //   • EventBus.Publish is safe (ConcurrentQueue). Flush() must be called
    //     sequentially after the parallel loop, as usual.
    // -------------------------------------------------------------------------

    /// <summary>Parallel variant of ForEach&lt;T&gt;. See preconditions above.</summary>
    public void ForEachParallel<T>(EcsAction<T> action) where T : struct
    {
        var store = Store<T>();
        int count = store.Count;
        var ids   = store.RawDenseIds;

        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            int id = ids[i];
            action(new Entity(id, _versions[id]), ref store.GetByDenseIndex(i));
        });
    }

    /// <summary>Parallel variant of ForEach&lt;T1, T2&gt;. See preconditions above.</summary>
    public void ForEachParallel<T1, T2>(EcsAction<T1, T2> action)
        where T1 : struct
        where T2 : struct
    {
        var s1    = Store<T1>();
        var s2    = Store<T2>();
        int count = s1.Count;
        var ids   = s1.RawDenseIds;

        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            int id = ids[i];
            if (!s2.Has(id)) return;
            action(
                new Entity(id, _versions[id]),
                ref s1.GetByDenseIndex(i),
                ref s2.GetByDenseIndex(s2.GetDenseIndex(id)));
        });
    }

    /// <summary>Parallel variant of ForEach&lt;T1, T2, T3&gt;. See preconditions above.</summary>
    public void ForEachParallel<T1, T2, T3>(EcsAction<T1, T2, T3> action)
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        var s1    = Store<T1>();
        var s2    = Store<T2>();
        var s3    = Store<T3>();
        int count = s1.Count;
        var ids   = s1.RawDenseIds;

        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            int id = ids[i];
            if (!s2.Has(id) || !s3.Has(id)) return;
            action(
                new Entity(id, _versions[id]),
                ref s1.GetByDenseIndex(i),
                ref s2.GetByDenseIndex(s2.GetDenseIndex(id)),
                ref s3.GetByDenseIndex(s3.GetDenseIndex(id)));
        });
    }

    // -------------------------------------------------------------------------
    // QueryIds — for when you need entity lists (e.g. to destroy during logic)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a snapshot list of all entity IDs that have component T.
    /// Safe to use even if entities are destroyed during iteration,
    /// because it copies IDs upfront.
    /// </summary>
    public List<Entity> QueryEntities<T>() where T : struct
    {
        var store = Store<T>();
        var result = new List<Entity>(store.Count);
        var ids = store.DenseIds;
        for (int i = 0; i < store.Count; i++)
        {
            int id = ids[i];
            result.Add(new Entity(id, _versions[id]));
        }
        return result;
    }

    /// <summary>Returns how many entities currently have component T.</summary>
    public int Count<T>() where T : struct => Store<T>().Count;

    // -------------------------------------------------------------------------
    // Deferred flush
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies all pending destroys and component removals.
    /// Call once per frame at a point where no system is iterating.
    /// </summary>
    public void FlushDeferred()
    {
        foreach (var (type, entityId) in _pendingRemoveComp)
            if (_stores.TryGetValue(type, out var store))
                ((ISparseSetEraser)store).RemoveById(entityId);
        _pendingRemoveComp.Clear();

        foreach (var e in _pendingDestroy)
            DestroyImmediate(e);
        _pendingDestroy.Clear();
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private SparseSet<T> Store<T>() where T : struct
    {
        var type = typeof(T);
        if (!_stores.TryGetValue(type, out var raw))
        {
            raw = new SparseSet<T>(_versions.Length);
            _stores[type] = raw;
        }
        return (SparseSet<T>)raw;
    }

    private void AssertAlive(Entity e)
    {
        if (!IsAlive(e))
            throw new InvalidOperationException($"{e} is not alive.");
    }
}
