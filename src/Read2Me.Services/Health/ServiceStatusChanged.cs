namespace Read2Me.Services.Health;

/// <summary>A manual lifecycle op on a managed service.</summary>
public enum AiServiceOp { Start, Restart, Shutdown }

/// <summary>
/// A managed service's status as last observed, published via
/// <c>EventBroadcaster&lt;ServiceStatusChanged&gt;</c> after every probe and every lifecycle op
/// that goes through <see cref="ObservedAiServiceControl"/>, so live clients see status flips
/// without polling. <paramref name="Op"/> with <paramref name="Ok"/> and <paramref name="Error"/>
/// is present only when an op produced the observation.
/// </summary>
public sealed record ServiceStatusChanged(
    string Service,
    AiServiceStatus Status,
    AiServiceOp? Op = null,
    bool? Ok = null,
    string? Error = null);
