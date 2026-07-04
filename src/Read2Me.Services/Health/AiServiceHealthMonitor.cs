using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Read2Me.Services.Events;

namespace Read2Me.Services.Health;

/// <summary>
/// The watchdog brain: turns failure reports into the pause → restart → poll → warm-up → resume
/// sequence. Report-driven only — never sweeps containers. One recovery runs at a time globally
/// (the GPU holds a single resident model), guarded by <see cref="AcquireExclusiveAsync"/>, which is
/// also the lock manual lifecycle operations (issue007) take.
/// </summary>
public sealed class AiServiceHealthMonitor
{
    private readonly IContainerController _controller;
    private readonly IAiServiceProbe _probe;
    private readonly WatchdogGateMap _gateMap;
    private readonly AiWatchdogOptions _options;
    private readonly EventBroadcaster<WatchdogEvent> _broadcaster;
    private readonly ILogger<AiServiceHealthMonitor> _log;

    private readonly ConcurrentDictionary<string, ServiceRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _single = new(1, 1);

    public AiServiceHealthMonitor(
        IContainerController controller,
        IAiServiceProbe probe,
        WatchdogGateMap gateMap,
        IOptions<AiWatchdogOptions> options,
        EventBroadcaster<WatchdogEvent> broadcaster,
        ILogger<AiServiceHealthMonitor> log)
    {
        _controller = controller;
        _probe = probe;
        _gateMap = gateMap;
        _options = options.Value;
        _broadcaster = broadcaster;
        _log = log;
    }

    /// <summary>A request against <paramref name="service"/> succeeded: clear its failure streak.</summary>
    public void ReportSuccess(DockerAiService service)
    {
        if (!_options.Enabled || !_gateMap.Contains(service.Name)) return;

        var record = _records.GetOrAdd(service.Name, static _ => new ServiceRecord());
        lock (record.Sync)
        {
            // A success while recovering/down means nothing — recovery owns the transition back.
            if (record.State is AiServiceState.Recovering or AiServiceState.Down) return;
            record.Failures = 0;
            record.State = AiServiceState.Healthy;
        }
    }

    /// <summary>
    /// A request against <paramref name="service"/> failed. Fire-and-forget: enough failures (or a
    /// <paramref name="tripImmediately"/> stall marked by issue006) trip recovery on a background task.
    /// </summary>
    public void ReportFailure(DockerAiService service, string reason, bool tripImmediately = false)
    {
        if (!_options.Enabled)
        {
            _log.LogDebug("Watchdog disabled; ignoring failure for {Service}: {Reason}", service.Name, reason);
            return;
        }

        if (!_gateMap.Contains(service.Name))
        {
            _log.LogDebug("Service {Service} is unmanaged; ignoring failure: {Reason}", service.Name, reason);
            return;
        }

        var record = _records.GetOrAdd(service.Name, static _ => new ServiceRecord());
        bool trip = false;
        lock (record.Sync)
        {
            // Already being recovered or given up on — concurrent reports collapse into one recovery.
            if (record.State is AiServiceState.Recovering or AiServiceState.Down) return;

            record.Failures++;
            if (tripImmediately || record.Failures >= _options.ConsecutiveFailuresToTrip)
            {
                record.State = AiServiceState.Recovering;
                trip = true;
            }
        }

        if (trip)
        {
            _ = Task.Run(() => RunRecoveryAsync(service, reason));
        }
    }

    /// <summary>Watchdog state for a service; <see cref="AiServiceState.Untracked"/> if unmanaged or never reported.</summary>
    public AiServiceState GetState(DockerAiService service)
    {
        if (!_gateMap.Contains(service.Name)) return AiServiceState.Untracked;
        if (_records.TryGetValue(service.Name, out var record))
        {
            lock (record.Sync) return record.State;
        }
        return AiServiceState.Untracked;
    }

    /// <summary>Manual all-clear (e.g. after a successful manual start/restart): Healthy, counter zeroed, gates opened.</summary>
    public void Reset(DockerAiService service)
    {
        var record = _records.GetOrAdd(service.Name, static _ => new ServiceRecord());
        lock (record.Sync)
        {
            record.State = AiServiceState.Healthy;
            record.Failures = 0;
        }
        foreach (var gate in _gateMap.GatesFor(service.Name)) gate.Open();
    }

    /// <summary>
    /// Zeroes a service's consecutive-failure streak without touching its state or gates. A deliberate
    /// shutdown (issue007) is not a failure, so it must not leave a partial streak that trips recovery
    /// on the next report. No-op if the service has no record yet.
    /// </summary>
    public void ClearFailures(DockerAiService service)
    {
        if (_records.TryGetValue(service.Name, out var record))
        {
            lock (record.Sync) record.Failures = 0;
        }
    }

    /// <summary>
    /// The global single-flight lock. Recovery and manual lifecycle ops (issue007) share it so two
    /// GPU services never restart/warm concurrently. Dispose the handle to release.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken ct)
    {
        await _single.WaitAsync(ct);
        return new Releaser(_single);
    }

    private async Task RunRecoveryAsync(DockerAiService service, string reason)
    {
        var ct = CancellationToken.None;
        var gates = _gateMap.GatesFor(service.Name);

        _broadcaster.Publish(new RecoveryStarted(service.Name, reason));
        foreach (var gate in gates) gate.Close($"Recovering {service.Name}: {reason}");

        string lastError = reason;
        bool recovered = false;

        // Hold the global lock across the whole sequence so a second tripped service waits its turn.
        await using (await AcquireExclusiveAsync(ct))
        {
            for (int attempt = 1; attempt <= _options.MaxRecoveryAttempts; attempt++)
            {
                var restart = await _controller.RestartAsync(service.ContainerName, ct);
                if (!restart.Succeeded)
                {
                    lastError = $"restart failed: {restart.Output}";
                    _log.LogWarning("Recovery attempt {Attempt} for {Service}: {Error}", attempt, service.Name, lastError);
                    continue;
                }
                _broadcaster.Publish(new ContainerRestarted(service.Name));

                if (!await _probe.WaitUntilHealthyAsync(service, ct))
                {
                    lastError = "health check timed out";
                    _log.LogWarning("Recovery attempt {Attempt} for {Service}: {Error}", attempt, service.Name, lastError);
                    continue;
                }

                if (!await _probe.WarmupAsync(service, ct))
                {
                    lastError = "warm-up failed";
                    _log.LogWarning("Recovery attempt {Attempt} for {Service}: {Error}", attempt, service.Name, lastError);
                    continue;
                }

                recovered = true;
                break;
            }
        }

        var record = _records.GetOrAdd(service.Name, static _ => new ServiceRecord());
        if (recovered)
        {
            lock (record.Sync)
            {
                record.State = AiServiceState.Healthy;
                record.Failures = 0;
            }
            foreach (var gate in gates) gate.Open();
            _broadcaster.Publish(new ServiceHealthy(service.Name));
            _log.LogInformation("Service {Service} recovered.", service.Name);
        }
        else
        {
            lock (record.Sync) record.State = AiServiceState.Down;
            var downReason = $"{service.Name} is down: {lastError}";
            // Gate is already closed with the "recovering" reason; Close is idempotent and won't
            // overwrite it, so reopen-then-close to leave the give-up reason in place.
            foreach (var gate in gates)
            {
                gate.Open();
                gate.Close(downReason);
            }
            _broadcaster.Publish(new ServiceDown(service.Name, lastError));
            _log.LogError("Service {Service} is down after {Attempts} recovery attempts: {Error}",
                service.Name, _options.MaxRecoveryAttempts, lastError);
        }
    }

    private sealed class ServiceRecord
    {
        public readonly object Sync = new();
        public AiServiceState State = AiServiceState.Healthy;
        public int Failures;
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
