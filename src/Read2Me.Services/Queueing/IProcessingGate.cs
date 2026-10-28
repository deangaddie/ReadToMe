using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Queueing;

/// <summary>
/// Pause seam for a queue worker: the worker awaits <see cref="WaitAsync"/> before pulling
/// the next item, so recovery can hold the queue without cancelling it. Starts open.
/// Each owner (see <see cref="GateOwners"/>) holds its own close: the gate stays closed while
/// any owner holds it, and an owner's <see cref="Open"/> releases only that owner's hold.
/// </summary>
public interface IProcessingGate<TItem>
{
    /// <summary>Completes immediately while the gate is open; blocks until every owner has opened it while closed.</summary>
    Task WaitAsync(CancellationToken ct);

    /// <summary>
    /// Adds <paramref name="owner"/>'s hold so waiters block. Idempotent per owner — closing again while
    /// that owner already holds the gate is a no-op and keeps the first reason.
    /// </summary>
    void Close(string owner, string reason);

    /// <summary>Releases <paramref name="owner"/>'s hold; the gate opens, releasing waiters, once no owner holds it.</summary>
    void Open(string owner);

    /// <summary>
    /// Replaces <paramref name="owner"/>'s reason in place while it keeps holding the gate, so the gate never
    /// opens between the old reason and the new. Closes the gate under that owner if it holds no hold yet.
    /// </summary>
    void ReplaceReason(string owner, string reason);

    bool IsOpen { get; }

    /// <summary>Every current owner's reason in the order they closed the gate, joined by "; "; null while open.</summary>
    string? CloseReason { get; }
}

/// <summary>The owners that close processing gates.</summary>
public static class GateOwners
{
    /// <summary>
    /// The AI service watchdog's hold for one service (recovery, give-up and deliberate shutdowns). Each
    /// service holds its own, so one service's recovery leaves another's hold on a shared gate in place.
    /// The name is lower-cased because service names match case-insensitively (see <c>WatchdogGateMap</c>).
    /// </summary>
    public static string WatchdogFor(string service) => $"watchdog:{service.ToLowerInvariant()}";
}
