using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Queueing;

/// <summary>
/// <see cref="IProcessingGate{TItem}"/> backed by a swapped <see cref="TaskCompletionSource"/>
/// (async manual-reset event, same hand-rolled style as the rest of the queueing code).
/// A null <c>_gate</c> means open; a pending TCS means closed. Close is idempotent; Open
/// releases all waiters.
/// </summary>
public sealed class ProcessingGate<TItem> : IProcessingGate<TItem>
{
    private readonly object _lock = new();
    private TaskCompletionSource? _gate; // null => open
    private string? _closeReason;

    public bool IsOpen
    {
        get { lock (_lock) return _gate is null; }
    }

    public string? CloseReason
    {
        get { lock (_lock) return _closeReason; }
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task wait;
        lock (_lock)
        {
            if (_gate is null) return Task.CompletedTask;
            wait = _gate.Task;
        }
        return wait.WaitAsync(ct);
    }

    public void Close(string reason)
    {
        lock (_lock)
        {
            if (_gate is not null) return; // already closed — idempotent
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeReason = reason;
        }
    }

    public void Open()
    {
        TaskCompletionSource? toRelease;
        lock (_lock)
        {
            toRelease = _gate;
            _gate = null;
            _closeReason = null;
        }
        toRelease?.TrySetResult();
    }
}
