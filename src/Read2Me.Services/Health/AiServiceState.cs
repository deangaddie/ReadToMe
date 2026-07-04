namespace Read2Me.Services.Health;

/// <summary>
/// Watchdog view of a single service. State exists only for services that have been reported to
/// (or explicitly operated on); everything else is <see cref="Untracked"/> — never probed on a
/// background sweep.
/// </summary>
public enum AiServiceState
{
    /// <summary>No reports seen — the watchdog holds no opinion and does nothing.</summary>
    Untracked,

    /// <summary>Last report succeeded (or a recovery completed); traffic flows.</summary>
    Healthy,

    /// <summary>A trip is being worked: gates closed, restart/health/warm-up in progress.</summary>
    Recovering,

    /// <summary>Recovery exhausted <c>MaxRecoveryAttempts</c>; gates stay closed until a manual reset.</summary>
    Down,
}
