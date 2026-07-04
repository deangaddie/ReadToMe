namespace Read2Me.Services.Health;

/// <summary>
/// Progress of a watchdog recovery, published via <c>EventBroadcaster&lt;WatchdogEvent&gt;</c> — same
/// transport as <c>AudioGenEvent</c>/<c>LlmStreamEvent</c>. UI consumption is out of scope here.
/// </summary>
public abstract record WatchdogEvent;

/// <summary>A trip fired for <paramref name="Service"/>; gates are being closed and recovery has begun.</summary>
public sealed record RecoveryStarted(string Service, string Reason) : WatchdogEvent;

/// <summary>The container was restarted successfully on the current attempt.</summary>
public sealed record ContainerRestarted(string Service) : WatchdogEvent;

/// <summary>Restart + health + warm-up all succeeded; gates reopened and traffic resumes.</summary>
public sealed record ServiceHealthy(string Service) : WatchdogEvent;

/// <summary>Recovery exhausted its attempts; the service is marked down and gates stay closed.</summary>
public sealed record ServiceDown(string Service, string LastError) : WatchdogEvent;
