namespace Read2Me.Services.Health;

/// <summary>
/// Tuning options for the AI service watchdog, bound from the <c>AiWatchdog</c> config section.
/// Defaults apply when the section is absent.
/// </summary>
public sealed class AiWatchdogOptions
{
    public const string SectionName = "AiWatchdog";

    /// <summary>Consecutive timeouts/connection failures that trip the watchdog for a service.</summary>
    public int ConsecutiveFailuresToTrip { get; set; } = 2;

    /// <summary>No-chunk inactivity window that trips a stalled stream (used by issue006).</summary>
    public int StreamInactivitySeconds { get; set; } = 120;

    /// <summary>Overall bound on polling the health endpoint after a restart.</summary>
    public int HealthPollTimeoutSeconds { get; set; } = 180;

    /// <summary>Delay between health-endpoint polls.</summary>
    public int HealthPollIntervalSeconds { get; set; } = 5;

    /// <summary>Overall bound on retrying the warm-up request.</summary>
    public int WarmupTimeoutSeconds { get; set; } = 300;

    /// <summary>Consecutive failed recoveries before the watchdog gives up and pauses the queue.</summary>
    public int MaxRecoveryAttempts { get; set; } = 2;

    /// <summary>Master switch; when false the watchdog stands aside entirely.</summary>
    public bool Enabled { get; set; } = true;
}
