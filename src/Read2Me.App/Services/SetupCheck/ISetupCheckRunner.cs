namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// Owns the Setup check report. The Quick check reads configuration and what is already running;
    /// it never starts, stops or switches anything. The Full check, run only when asked, sends one real
    /// request per dependency; one runs per host.
    /// </summary>
    public interface ISetupCheckRunner
    {
        /// <summary>The last report; <see cref="SetupCheckReport.Empty"/> until the first Quick check ends.</summary>
        SetupCheckReport Report { get; }

        /// <summary>
        /// Runs the Quick check and answers the new report. While a Full check runs it answers the
        /// current report instead: the run owns the rows until it ends.
        /// </summary>
        Task<SetupCheckReport> RunQuickCheckAsync(CancellationToken ct);

        /// <summary>
        /// Starts the Full check in the background, or joins the Full check already going. Refused,
        /// starting nothing, while the Audio or Character queue holds items or a voice batch, discovery
        /// or assembly runs, and while one check re-runs.
        /// </summary>
        Task<FullCheckStart> StartFullCheckAsync();

        /// <summary>
        /// Re-runs one Full-tier check by row id in the background: only the steps that judge it (and
        /// feed it: similarity transcribes first), under the Full check's refusal, gate, watchdog and
        /// put-back rules. Refused while a Full check or another re-run is going, before the start-up
        /// Quick check ends, for a row no step judges, and when the row's rerun.gpu says no GPU but the
        /// check now drives one. An id that isn't a Full-tier row sets <see cref="FullCheckStart.UnknownCheck"/>.
        /// </summary>
        Task<FullCheckStart> RerunCheckAsync(string checkId);

        /// <summary>
        /// Asks the run going to stop once its current step ends; the remaining checks read Not run.
        /// Null when no Full check is running.
        /// </summary>
        SetupCheckReport? CancelFullCheck();
    }

    /// <summary>
    /// What asking for a Full check did. <see cref="Refusal"/>: why it didn't start (nothing ran).
    /// <see cref="Joined"/>: a run was already going and this joined it. <see cref="Finished"/>
    /// completes with the final report when the run ends (at once on a refusal).
    /// <see cref="UnknownCheck"/>: refused because a re-run named no Full-tier row.
    /// </summary>
    public sealed record FullCheckStart(
        SetupCheckReport Report, bool Joined, string? Refusal, Task<SetupCheckReport> Finished, bool UnknownCheck = false);
}
