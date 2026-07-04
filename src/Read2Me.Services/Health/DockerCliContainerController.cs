using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Health;

/// <summary>
/// <see cref="IContainerController"/> implemented by shelling out to the <c>docker</c> CLI via
/// <see cref="IProcessRunner"/> — no package dependency, works wherever docker is on PATH. Non-zero
/// exit codes and launch failures (docker not installed) yield <c>Succeeded = false</c> / <c>Unknown</c>
/// rather than throwing; cancellation propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DockerCliContainerController : IContainerController
{
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _runner;
    private readonly ILogger<DockerCliContainerController> _logger;

    public DockerCliContainerController(IProcessRunner runner, ILogger<DockerCliContainerController> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public Task<ContainerOpResult> StartAsync(string containerName, CancellationToken ct) =>
        RunLifecycleAsync("start", containerName, ct);

    public Task<ContainerOpResult> StopAsync(string containerName, CancellationToken ct) =>
        RunLifecycleAsync("stop", containerName, ct);

    public Task<ContainerOpResult> RestartAsync(string containerName, CancellationToken ct) =>
        RunLifecycleAsync("restart", containerName, ct);

    public async Task<ContainerRunState> GetStateAsync(string containerName, CancellationToken ct)
    {
        try
        {
            var (exitCode, output) = await _runner.RunAsync(
                "docker",
                $"inspect -f \"{{{{.State.Status}}}}\" {containerName}",
                InspectTimeout,
                ct);

            if (exitCode != 0)
            {
                if (output.Contains("No such object", StringComparison.OrdinalIgnoreCase))
                {
                    return ContainerRunState.NotFound;
                }

                _logger.LogWarning("docker inspect {Container} exited {Code}: {Output}", containerName, exitCode, output);
                return ContainerRunState.Unknown;
            }

            return MapStatus(output.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker inspect {Container} failed to launch", containerName);
            return ContainerRunState.Unknown;
        }
    }

    private async Task<ContainerOpResult> RunLifecycleAsync(string verb, string containerName, CancellationToken ct)
    {
        try
        {
            var (exitCode, output) = await _runner.RunAsync(
                "docker", $"{verb} {containerName}", LifecycleTimeout, ct);

            if (exitCode == 0)
            {
                return new ContainerOpResult(true, output);
            }

            _logger.LogWarning("docker {Verb} {Container} exited {Code}: {Output}", verb, containerName, exitCode, output);
            return new ContainerOpResult(false, output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker {Verb} {Container} failed to launch", verb, containerName);
            return new ContainerOpResult(false, ex.Message);
        }
    }

    private static ContainerRunState MapStatus(string status) => status.ToLowerInvariant() switch
    {
        "running" => ContainerRunState.Running,
        "exited" or "created" or "paused" or "dead" or "restarting" => ContainerRunState.Stopped,
        _ => ContainerRunState.Unknown,
    };
}
