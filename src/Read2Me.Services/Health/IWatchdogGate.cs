using Read2Me.Services.Queueing;

namespace Read2Me.Services.Health;

/// <summary>
/// Non-generic control surface over an <see cref="IProcessingGate{TItem}"/>, so the monitor can hold
/// a heterogeneous map of gates keyed by service name without knowing each queue's item type.
/// Several services can share one gate; each holds and releases only its own hold on it.
/// </summary>
public interface IWatchdogGate
{
    /// <summary>Adds <paramref name="service"/>'s hold; a no-op that keeps the first reason if it already holds it.</summary>
    void Close(string service, string reason);

    /// <summary>Releases <paramref name="service"/>'s hold only; another service's or owner's hold stays.</summary>
    void Open(string service);

    /// <summary>Replaces <paramref name="service"/>'s reason while it keeps holding the gate, so the gate never opens in between.</summary>
    void ReplaceReason(string service, string reason);

    bool IsOpen { get; }
    string? CloseReason { get; }

    /// <summary>
    /// Whether the queue behind this gate has items still waiting. Lets a deliberate shutdown close
    /// the gate (so pending items are not burned) while leaving an idle queue to stop quietly.
    /// </summary>
    bool HasPendingWork { get; }
}

/// <summary>
/// Adapts a typed <see cref="IProcessingGate{TItem}"/> to the non-generic <see cref="IWatchdogGate"/>.
/// Each service closes and opens its own <see cref="GateOwners.WatchdogFor"/> hold, so one service's
/// recovery, and any other owner's close, survives another service reopening the gate.
/// <see cref="IsOpen"/> and <see cref="CloseReason"/> report every owner's holds.
/// </summary>
public sealed class ProcessingGateAdapter<TItem>(IProcessingGate<TItem> gate, IQueueSource<TItem> source) : IWatchdogGate
{
    public void Close(string service, string reason) => gate.Close(GateOwners.WatchdogFor(service), reason);
    public void Open(string service) => gate.Open(GateOwners.WatchdogFor(service));
    public void ReplaceReason(string service, string reason) => gate.ReplaceReason(GateOwners.WatchdogFor(service), reason);
    public bool IsOpen => gate.IsOpen;
    public string? CloseReason => gate.CloseReason;
    public bool HasPendingWork => source.Reader.CanCount && source.Reader.Count > 0;
}
