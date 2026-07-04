using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Health;

/// <summary>
/// Side-effecting lifecycle primitives over a single named Docker container, plus an on-demand
/// status query. No background polling — state is fetched only when asked, for the named container.
/// </summary>
public interface IContainerController
{
    Task<ContainerOpResult> StartAsync(string containerName, CancellationToken ct);
    Task<ContainerOpResult> StopAsync(string containerName, CancellationToken ct);
    Task<ContainerOpResult> RestartAsync(string containerName, CancellationToken ct);
    Task<ContainerRunState> GetStateAsync(string containerName, CancellationToken ct);
}

/// <summary>
/// Outcome of a lifecycle operation. <see cref="Output"/> carries the combined stdout/stderr for
/// logging and the give-up message.
/// </summary>
public sealed record ContainerOpResult(bool Succeeded, string Output);

/// <summary>Run state of a container as reported by <c>docker inspect</c>.</summary>
public enum ContainerRunState
{
    /// <summary>Container is up and running.</summary>
    Running,

    /// <summary>Container exists but is not running (exited/created/paused).</summary>
    Stopped,

    /// <summary>No such container — image never built/composed.</summary>
    NotFound,

    /// <summary>Docker CLI unavailable or errored.</summary>
    Unknown,
}
