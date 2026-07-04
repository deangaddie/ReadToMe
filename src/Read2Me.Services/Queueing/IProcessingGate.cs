using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Queueing;

/// <summary>
/// Pause seam for a queue worker: the worker awaits <see cref="WaitAsync"/> before pulling
/// the next item, so recovery can hold the queue without cancelling it. Starts open.
/// </summary>
public interface IProcessingGate<TItem>
{
    /// <summary>Completes immediately while the gate is open; blocks until <see cref="Open"/> while closed.</summary>
    Task WaitAsync(CancellationToken ct);

    /// <summary>Closes the gate so waiters block. Idempotent — closing an already-closed gate is a no-op.</summary>
    void Close(string reason);

    /// <summary>Opens the gate and releases all pending waiters.</summary>
    void Open();

    bool IsOpen { get; }

    string? CloseReason { get; }
}
