using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Queueing;

/// <summary>
/// <see cref="IProcessingGate{TItem}"/> backed by a swapped <see cref="TaskCompletionSource"/>
/// (async manual-reset event, same hand-rolled style as the rest of the queueing code).
/// A null <c>_gate</c> means open; a pending TCS means closed, held by one or more owners in
/// <c>_holds</c>. Close is idempotent per owner; the last owner's Open releases all waiters.
/// </summary>
public sealed class ProcessingGate<TItem> : IProcessingGate<TItem>
{
    private readonly object _lock = new();
    private readonly List<(string Owner, string Reason)> _holds = []; // in close order
    private TaskCompletionSource? _gate; // null => open

    public bool IsOpen
    {
        get { lock (_lock) return _gate is null; }
    }

    public string? CloseReason
    {
        get
        {
            lock (_lock) return _holds.Count == 0 ? null : string.Join("; ", _holds.Select(h => h.Reason));
        }
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

    public void Close(string owner, string reason)
    {
        lock (_lock)
        {
            if (_holds.Exists(h => h.Owner == owner)) return; // this owner already holds it — idempotent
            _holds.Add((owner, reason));
            _gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Open(string owner)
    {
        TaskCompletionSource? toRelease = null;
        lock (_lock)
        {
            _holds.RemoveAll(h => h.Owner == owner);
            if (_holds.Count == 0)
            {
                toRelease = _gate;
                _gate = null;
            }
        }
        toRelease?.TrySetResult();
    }
}
