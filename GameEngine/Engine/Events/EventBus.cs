namespace AsteroidsEngine.Engine.Events;

/// <summary>
/// Typed publish-subscribe event bus with deferred dispatch.
/// Events published during system updates are queued and dispatched
/// via Flush() at a safe point (after all systems have run).
///
/// Full implementation in Phase 4. This stub compiles and runs correctly —
/// Flush() dispatches all queued events synchronously.
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<object>> _subscribers = new();
    // ConcurrentQueue allows Model-3 systems to Publish from parallel bodies
    // without additional synchronisation. Flush() is always called sequentially.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(Type type, object evt)> _queue = new();

    // -------------------------------------------------------------------------
    // Subscription
    // -------------------------------------------------------------------------

    public void Subscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        if (!_subscribers.TryGetValue(type, out var list))
            _subscribers[type] = list = new List<object>();
        list.Add(handler);
    }

    public void Unsubscribe<T>(Action<T> handler)
    {
        if (_subscribers.TryGetValue(typeof(T), out var list))
            list.Remove(handler);
    }

    // -------------------------------------------------------------------------
    // Publishing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Queues an event for dispatch on the next Flush().
    /// Thread-safe: safe to call from ForEachParallel bodies (Model 3).
    /// </summary>
    public void Publish<T>(T evt) where T : notnull =>
        _queue.Enqueue((typeof(T), evt));

    /// <summary>
    /// Dispatches immediately (bypasses the queue).
    /// Use sparingly — only when a reaction must happen within the same frame tick.
    /// </summary>
    public void PublishImmediate<T>(T evt) where T : notnull =>
        Dispatch(typeof(T), evt);

    // -------------------------------------------------------------------------
    // Flush — call once per frame at a safe point
    // -------------------------------------------------------------------------

    public void Flush()
    {
        while (_queue.TryDequeue(out var item))
            Dispatch(item.type, item.evt);
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private void Dispatch(Type type, object evt)
    {
        if (!_subscribers.TryGetValue(type, out var list)) return;

        // Iterate a snapshot in case a subscriber adds/removes subscriptions.
        var snapshot = list.ToArray();
        foreach (var handler in snapshot)
            ((Delegate)handler).DynamicInvoke(evt);
    }
}
