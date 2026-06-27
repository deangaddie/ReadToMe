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


}
