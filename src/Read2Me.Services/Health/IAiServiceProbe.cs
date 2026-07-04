using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// The "is it back, and is the model loaded" half of watchdog recovery: polls a service's
/// health endpoint after a restart, then fires its warm-up request to force the model load
/// before real traffic resumes.
/// </summary>
public interface IAiServiceProbe
{
    /// <summary>
    /// Polls <c>BaseUrl + HealthPath</c> until a 2xx arrives or the health-poll timeout elapses.
    /// Connection refused, timeouts and non-2xx responses are all just "not yet".
    /// Returns false on overall timeout.
    /// </summary>
    Task<bool> WaitUntilHealthyAsync(DockerAiService service, CancellationToken ct);

    /// <summary>
    /// One health GET against <c>BaseUrl + HealthPath</c>, no retry loop. True on 2xx; false on any
    /// non-2xx, connection refused or timeout. Used by the status facade to split a running
    /// container into <c>Starting</c> vs <c>Ready</c> cheaply.
    /// </summary>
    Task<bool> IsHealthyAsync(DockerAiService service, CancellationToken ct);

    /// <summary>
    /// Invokes the service's <see cref="DockerAiService.Warmup"/> delegate under the warm-up
    /// timeout. A service with no delegate is ready on health alone and returns true immediately.
    /// Any exception (including timeout) is swallowed and returns false.
    /// </summary>
    Task<bool> WarmupAsync(DockerAiService service, CancellationToken ct);
}
