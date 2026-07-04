using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Health;

/// <summary>
/// Default <see cref="IAiServiceControl"/>. Manual lifecycle operations reuse the recovery machinery:
/// they take the monitor's exclusive lock, then run the same start/restart → health poll → warm-up
/// sequence, so the model is resident when a <c>Ready</c> result comes back and no two GPU services
/// operate at once.
/// </summary>
public sealed class AiServiceControl(
    DockerAiServiceRegistry registry,
    IContainerController controller,
    IAiServiceProbe probe,
    WatchdogGateMap gateMap,
    AiServiceHealthMonitor monitor,
    ILogger<AiServiceControl> log) : IAiServiceControl
{
    public DockerAiService? Resolve(string baseUrl) =>
        registry.TryGetByBaseUrl(baseUrl, out var service) ? service : null;

    public async Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct)
    {
        // Watchdog opinion wins outright — it knows more than a point-in-time container probe.
        switch (monitor.GetState(service))
        {
            case AiServiceState.Recovering: return AiServiceStatus.Recovering;
            case AiServiceState.Down: return AiServiceStatus.Down;
        }

        // On-demand, single container: never sweep the registry (only 1–2 containers run at a time).
        var run = await controller.GetStateAsync(service.ContainerName, ct);
        return run switch
        {
            ContainerRunState.NotFound => AiServiceStatus.NotFound,
            ContainerRunState.Stopped => AiServiceStatus.Stopped,
            ContainerRunState.Unknown => AiServiceStatus.Unknown,
            // Running: one health GET splits "model still loading" from "ready". No retry loop.
            ContainerRunState.Running => await probe.IsHealthyAsync(service, ct)
                ? AiServiceStatus.Ready
                : AiServiceStatus.Starting,
            _ => AiServiceStatus.Unknown,
        };
    }

    public Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct) =>
        BringUpAsync(service, restart: false, ct);

    public Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct) =>
        BringUpAsync(service, restart: true, ct);

    private async Task<AiServiceOpResult> BringUpAsync(DockerAiService service, bool restart, CancellationToken ct)
    {
        var verb = restart ? "restart" : "start";

        // The exclusive lock is the same one recovery holds — a manual op waits for an in-flight
        // recovery (or another manual op) and never runs a second lifecycle sequence concurrently.
        await using (await monitor.AcquireExclusiveAsync(ct))
        {
            var op = restart
                ? await controller.RestartAsync(service.ContainerName, ct)
                : await controller.StartAsync(service.ContainerName, ct);
            if (!op.Succeeded)
                return await FailureAsync(service, $"docker {verb} failed: {op.Output}", ct);

            if (!await probe.WaitUntilHealthyAsync(service, ct))
                return await FailureAsync(service, "health check timed out", ct);

            if (!await probe.WarmupAsync(service, ct))
                return await FailureAsync(service, "warm-up failed", ct);
        }

        // Clears any Down state, zeroes the failure counter and reopens the gates.
        monitor.Reset(service);
        log.LogInformation("Manual {Verb} of {Service} succeeded.", verb, service.Name);
        return new AiServiceOpResult(true, AiServiceStatus.Ready, null);
    }

    public async Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct)
    {
        await using (await monitor.AcquireExclusiveAsync(ct))
        {
            var op = await controller.StopAsync(service.ContainerName, ct);
            if (!op.Succeeded)
                return await FailureAsync(service, $"docker stop failed: {op.Output}", ct);

            // An intentional stop must not let a running queue burn its items — close the gate so the
            // worker parks. An empty/idle queue just stops quietly (no reason to pause nothing).
            foreach (var gate in gateMap.GatesFor(service.Name))
            {
                if (gate.HasPendingWork)
                    gate.Close($"{service.Name} was shut down");
            }

            // A deliberate stop is not a failure; don't leave a streak that trips recovery next report.
            monitor.ClearFailures(service);
        }

        log.LogInformation("Manual shutdown of {Service} succeeded.", service.Name);
        return new AiServiceOpResult(true, AiServiceStatus.Stopped, null);
    }

    /// <summary>Report the true resulting status without touching monitor state (no <c>Reset</c>).</summary>
    private async Task<AiServiceOpResult> FailureAsync(DockerAiService service, string error, CancellationToken ct)
    {
        log.LogWarning("Manual operation on {Service} failed: {Error}", service.Name, error);
        var status = await GetStatusAsync(service, ct);
        return new AiServiceOpResult(false, status, error);
    }
}
