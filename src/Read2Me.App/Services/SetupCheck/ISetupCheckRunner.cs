namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// Owns the Setup check report. The Quick check reads configuration and what is already running;
    /// it never starts, stops or switches anything.
    /// </summary>
    public interface ISetupCheckRunner
    {
        /// <summary>The last report; <see cref="SetupCheckReport.Empty"/> until the first Quick check ends.</summary>
        SetupCheckReport Report { get; }

        /// <summary>Runs the Quick check and answers the new report.</summary>
        Task<SetupCheckReport> RunQuickCheckAsync(CancellationToken ct);
    }
}
