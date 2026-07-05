namespace Read2Me.Services.Events;

/// <summary>
/// Buffers every event of the current "turn" (from the most recent turn-start event onward)
/// so a late subscriber — e.g. a stream view expanded mid-request — can replay what it
/// missed. The buffer resets when the next turn starts, so at most one turn is retained.
/// <see cref="Subscribe"/> replays the buffer and attaches the handler atomically with
/// respect to <c>Publish</c>: no event is dropped or delivered twice around the handoff.
/// Must be created at application start (before any events flow) to observe every turn.
/// </summary>
public sealed class EventJournal<T>
{
    private readonly Func<T, bool> _isTurnStart;
    private readonly object _gate = new();
    private readonly List<T> _currentTurn = [];
    private event Action<T>? Event;

    public EventJournal(EventBroadcaster<T> broadcaster, Func<T, bool> isTurnStart)
    {
        _isTurnStart = isTurnStart;
        broadcaster.Event += OnPublished;
    }

    private void OnPublished(T e)
    {
        lock (_gate)
        {
            if (_isTurnStart(e))
                _currentTurn.Clear();
            _currentTurn.Add(e);
            Event?.Invoke(e);
        }
    }

    /// <summary>Replays the buffered turn to <paramref name="handler"/>, then subscribes it for live events.</summary>
    public void Subscribe(Action<T> handler)
    {
        lock (_gate)
        {
            foreach (var e in _currentTurn)
                handler(e);
            Event += handler;
        }
    }

    public void Unsubscribe(Action<T> handler)
    {
        lock (_gate)
        {
            Event -= handler;
        }
    }
}
