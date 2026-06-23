using System;

namespace Read2Me.Services.Audio.Assembly
{
    public abstract record AssemblyEvent;

    public sealed record AssemblyPhaseStarted(AssemblyPhase Phase) : AssemblyEvent;
    public sealed record AssemblyEncodeProgress(double Fraction) : AssemblyEvent;
    public sealed record AssemblyCompleted : AssemblyEvent;
    public sealed record AssemblyFailed(string Reason) : AssemblyEvent;
    public sealed record AssemblyCancelled : AssemblyEvent;

    public enum AssemblyPhase
    {
        Gather,
        Silence,
        ProbeConcat,
        Encode,
        Finalize,
    }

    /// Singleton bridge: background assembly job publishes; status bar subscribes.
    public sealed class AudiobookAssemblyBroadcaster
    {
        public event Action<AssemblyEvent>? Event;
        public void Publish(AssemblyEvent e) => Event?.Invoke(e);
    }
}
