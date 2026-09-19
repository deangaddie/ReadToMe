namespace Read2Me.Services.Audio.Assembly
{
    public abstract record AssemblyEvent
    {
        /// <summary>
        /// The project the run belongs to. Stamped where the event is published: assembly is one
        /// global run, and a consumer that reads the service's state later may already see the next one.
        /// </summary>
        public string? Folder { get; init; }
    }

    public sealed record AssemblyPhaseStarted(AssemblyPhase Phase) : AssemblyEvent;
    public sealed record AssemblyEncodeProgress(double Fraction) : AssemblyEvent;
    /// <summary><paramref name="OutputFileName"/> is the m4b's name under the project's <c>output/</c> folder.</summary>
    public sealed record AssemblyCompleted(string OutputFileName) : AssemblyEvent;
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
