namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The work a Full check must never disrupt: items in the Audio or Character queue, and a voice
    /// batch, character discovery or audiobook assembly running. Each is read live, so a refusal
    /// names what is running now and how to stop it.
    /// </summary>
    public sealed class InFlightWork(
        Func<int> audioItems,
        Func<int> characterParagraphs,
        Func<bool> voiceBatchRunning,
        Func<bool> discoveryRunning,
        Func<bool> assemblyRunning)
    {
        private const string CancelInJobs = "cancel it in Activity → Jobs";

        /// <summary>One phrase per kind of work in flight, each saying how to stop it; empty when nothing runs.</summary>
        public IReadOnlyList<string> Describe()
        {
            var running = new List<string>();
            if (audioItems() is var audio and > 0)
                running.Add($"the Audio queue holds {Count(audio, "item")} — {CancelInJobs}");
            if (characterParagraphs() is var paragraphs and > 0)
                running.Add($"the Character queue holds {Count(paragraphs, "paragraph")} — {CancelInJobs}");
            if (voiceBatchRunning())
                running.Add($"a voice batch is running — {CancelInJobs}");
            if (discoveryRunning())
                running.Add("character discovery is running — wait for it to finish");
            if (assemblyRunning())
                running.Add($"audiobook assembly is running — {CancelInJobs}");
            return running;
        }

        /// <summary>Why a Full check can't start now, or null when nothing is in flight.</summary>
        public string? Refusal() => Describe() is { Count: > 0 } running
            ? $"The Full check can't start while other work is running: {string.Join("; ", running)}."
            : null;

        private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    /// <summary>
    /// Whether a Full check is driving the AI services, which Preflight reads to refuse new AI tasks
    /// with <see cref="Reason"/> until it ends.
    /// </summary>
    public sealed class SetupCheckBusy
    {
        /// <summary>What every queue gate and every refused AI task says while a Full check runs.</summary>
        public const string Reason = "Setup check running";

        private volatile bool _running;

        public bool Running => _running;

        internal void Set(bool running) => _running = running;
    }
}
