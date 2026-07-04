using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// The single facade the AI settings UI (issue008) calls per configuration. Combines the registry,
/// container controller, health probe and monitor so that manual Start/Restart/Shutdown share the
/// monitor's exclusive lock with automatic recovery — two GPU services never restart or warm
/// concurrently, and a manual op never overlaps an in-flight recovery.
/// </summary>
public interface IAiServiceControl
{
    /// <summary>
    /// The managed service for a base URL, or null when it misses the registry (a remote endpoint) —
    /// the UI then shows no lifecycle controls.
    /// </summary>
    DockerAiService? Resolve(string baseUrl);

    /// <summary>On-demand status for exactly one service; a single health probe, never a registry sweep.</summary>
    Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct);

    /// <summary>Exclusive: docker start → health poll → warm-up → reset. <c>Ready</c> on success.</summary>
    Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct);

    /// <summary>Exclusive: docker restart → health poll → warm-up → reset. <c>Ready</c> on success.</summary>
    Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct);

    /// <summary>Exclusive: docker stop; closes the mapped gate if its queue has pending work.</summary>
    Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct);
}

/// <summary>Live status of a managed service as the settings UI displays it.</summary>
public enum AiServiceStatus
{
    /// <summary>No such container — never built/composed.</summary>
    NotFound,

    /// <summary>Container exists but is not running.</summary>
    Stopped,

    /// <summary>Container running, health endpoint not yet answering (model still loading).</summary>
    Starting,

    /// <summary>Container running and health endpoint returning 2xx.</summary>
    Ready,

    /// <summary>The watchdog is actively recovering this service.</summary>
    Recovering,

    /// <summary>Recovery gave up; the queue is paused until a manual start/restart.</summary>
    Down,

    /// <summary>Docker CLI unavailable or errored — state indeterminate.</summary>
    Unknown,
}

/// <summary>Outcome of a lifecycle operation, with the resulting status and an error on failure.</summary>
/// <param name="Succeeded">Whether the whole sequence completed.</param>
/// <param name="Status">Status the service is in after the operation.</param>
/// <param name="Error">Human-readable failure reason; null on success.</param>
public sealed record AiServiceOpResult(bool Succeeded, AiServiceStatus Status, string? Error);
