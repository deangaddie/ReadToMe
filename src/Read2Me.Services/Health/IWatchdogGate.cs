using Read2Me.Services.Queueing;

namespace Read2Me.Services.Health;

/// <summary>
/// Non-generic control surface over an <see cref="IProcessingGate{TItem}"/>, so the monitor can hold
/// a heterogeneous map of gates keyed by service name without knowing each queue's item type.
/// </summary>
public interface IWatchdogGate
{
    void Close(string reason);
    void Open();
    bool IsOpen { get; }
    string? CloseReason { get; }

    /// <summary>
    /// Whether the queue behind this gate has items still waiting. Lets a deliberate shutdown close
    /// the gate (so pending items are not burned) while leaving an idle queue to stop quietly.
    /// </summary>
    bool HasPendingWork { get; }
}

/// <summary>Adapts a typed <see cref="IProcessingGate{TItem}"/> to the non-generic <see cref="IWatchdogGate"/>.</summary>
public sealed class ProcessingGateAdapter<TItem>(IProcessingGate<TItem> gate, IQueueSource<TItem> source) : IWatchdogGate
{
    public void Close(string reason) => gate.Close(reason);
    public void Open() => gate.Open();
    public bool IsOpen => gate.IsOpen;
    public string? CloseReason => gate.CloseReason;
    public bool HasPendingWork => source.Reader.CanCount && source.Reader.Count > 0;
}
