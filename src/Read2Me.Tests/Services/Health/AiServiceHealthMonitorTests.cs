using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class AiServiceHealthMonitorTests
{
    private static readonly DockerAiService Llama = new("llama", "read2me-llama", "http://localhost:8080", "/health");
    private static readonly DockerAiService Tts = new("voxcpm2", "read2me-voxcpm2", "http://localhost:8003", "/docs");

    // ---- Fakes ------------------------------------------------------------

    /// <summary>Mirrors real <c>ProcessingGate</c> semantics: idempotent close, open clears reason.</summary>
    private sealed class FakeGate : IWatchdogGate
    {
        private readonly List<string> _log;
        private readonly string _name;
        public FakeGate(List<string> log, string name) { _log = log; _name = name; }

        public bool IsOpen { get; private set; } = true;
        public string? CloseReason { get; private set; }
        public bool HasPendingWork { get; set; }
        public int CloseCount { get; private set; }
        public int OpenCount { get; private set; }

        public void Close(string reason)
        {
            if (!IsOpen) return; // idempotent, like the real gate — reason not overwritten
            IsOpen = false;
            CloseReason = reason;
            CloseCount++;
            _log.Add($"close:{_name}");
        }

        public void Open()
        {
            IsOpen = true;
            CloseReason = null;
            OpenCount++;
            _log.Add($"open:{_name}");
        }
    }

    private sealed class FakeController : IContainerController
    {
        private readonly List<string>? _log;
        private readonly object _sync = new();
        public FakeController(List<string>? log = null) => _log = log;

        public List<string> RestartedContainers { get; } = new();
        public int MaxConcurrency { get; private set; }
        private int _live;

        /// <summary>Per-container hook to force a failure or block for the concurrency test.</summary>
        public Func<string, Task<ContainerOpResult>>? RestartImpl { get; set; }

        public async Task<ContainerOpResult> RestartAsync(string containerName, CancellationToken ct)
        {
            lock (_sync)
            {
                _live++;
                MaxConcurrency = Math.Max(MaxConcurrency, _live);
                RestartedContainers.Add(containerName);
                _log?.Add("restart");
            }
            try
            {
                if (RestartImpl is not null) return await RestartImpl(containerName);
                await Task.Yield();
                return new ContainerOpResult(true, "ok");
            }
            finally
            {
                lock (_sync) _live--;
            }
        }

        public Task<ContainerOpResult> StartAsync(string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerOpResult> StopAsync(string c, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerRunState> GetStateAsync(string c, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeProbe : IAiServiceProbe
    {
        public Func<DockerAiService, Task<bool>> HealthImpl = _ => Task.FromResult(true);
        public Func<DockerAiService, Task<bool>> WarmupImpl = _ => Task.FromResult(true);

        public Task<bool> WaitUntilHealthyAsync(DockerAiService service, CancellationToken ct) => HealthImpl(service);
        public Task<bool> IsHealthyAsync(DockerAiService service, CancellationToken ct) => HealthImpl(service);
        public Task<bool> WarmupAsync(DockerAiService service, CancellationToken ct) => WarmupImpl(service);
    }

    // ---- Harness ----------------------------------------------------------

    private sealed class Harness
    {
        public required AiServiceHealthMonitor Monitor { get; init; }
        public required FakeController Controller { get; init; }
        public required FakeProbe Probe { get; init; }
        public required List<WatchdogEvent> Events { get; init; }
        public required List<string> OrderLog { get; init; }
        public required IReadOnlyDictionary<string, FakeGate> Gates { get; init; }

        private readonly EventBroadcaster<WatchdogEvent> _broadcaster;
        public Harness(EventBroadcaster<WatchdogEvent> broadcaster) => _broadcaster = broadcaster;

        /// <summary>Runs <paramref name="trigger"/> and waits for the next terminal (Healthy/Down) event.</summary>
        public async Task AwaitRecovery(Action trigger)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(WatchdogEvent e)
            {
                if (e is ServiceHealthy or ServiceDown) done.TrySetResult();
            }
            _broadcaster.Event += Handler;
            try
            {
                trigger();
                await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _broadcaster.Event -= Handler;
            }
        }
    }

    private static Harness CreateHarness(
        AiWatchdogOptions? options = null,
        params DockerAiService[] mappedServices)
    {
        var services = mappedServices.Length > 0 ? mappedServices : new[] { Llama, Tts };
        var orderLog = new List<string>();
        var gates = services.ToDictionary(s => s.Name, s => new FakeGate(orderLog, s.Name), StringComparer.OrdinalIgnoreCase);
        var map = new WatchdogGateMap(services.ToDictionary(
            s => s.Name,
            s => (IReadOnlyList<IWatchdogGate>)new IWatchdogGate[] { gates[s.Name] },
            StringComparer.OrdinalIgnoreCase));

        var controller = new FakeController(orderLog);
        var probe = new FakeProbe();
        var broadcaster = new EventBroadcaster<WatchdogEvent>();
        var events = new List<WatchdogEvent>();
        broadcaster.Event += events.Add;

        var monitor = new AiServiceHealthMonitor(
            controller, probe, map,
            Options.Create(options ?? new AiWatchdogOptions()),
            broadcaster,
            NullLogger<AiServiceHealthMonitor>.Instance);

        return new Harness(broadcaster)
        {
            Monitor = monitor,
            Controller = controller,
            Probe = probe,
            Events = events,
            OrderLog = orderLog,
            Gates = gates,
        };
    }

    // ---- Tests ------------------------------------------------------------

    [Fact]
    public void BelowThresholdFailure_DoesNotTrip()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 2 });

        h.Monitor.ReportFailure(Llama, "timeout");

        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
        Assert.Empty(h.Events);
        Assert.True(h.Gates["llama"].IsOpen);
        Assert.Empty(h.Controller.RestartedContainers);
    }

    [Fact]
    public void ReportSuccess_ResetsFailureCounter()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 2 });

        h.Monitor.ReportFailure(Llama, "timeout");
        h.Monitor.ReportSuccess(Llama); // resets the streak
        h.Monitor.ReportFailure(Llama, "timeout"); // only the first of a fresh pair

        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
        Assert.Empty(h.Controller.RestartedContainers);
    }

    [Fact]
    public async Task Trip_ClosesGateBeforeRestart_ReopensAfterAllSucceed()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1 });

        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Llama, "boom"));

        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
        Assert.True(h.Gates["llama"].IsOpen);
        // close happens before restart; open only after restart+health+warmup succeed.
        Assert.Equal(new[] { "close:llama", "restart", "open:llama" }, h.OrderLog);
        Assert.IsType<RecoveryStarted>(h.Events[0]);
        Assert.IsType<ContainerRestarted>(h.Events[1]);
        Assert.IsType<ServiceHealthy>(h.Events[2]);
    }

    [Fact]
    public async Task LlamaTrip_ClosesOnlyParagraphGate_TtsTrip_ClosesOnlyAudioGate()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1 });

        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Llama, "boom"));
        Assert.Equal(1, h.Gates["llama"].CloseCount);
        Assert.Equal(0, h.Gates["voxcpm2"].CloseCount);

        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Tts, "boom"));
        Assert.Equal(1, h.Gates["voxcpm2"].CloseCount);
        Assert.Equal(1, h.Gates["llama"].CloseCount); // unchanged
    }

    [Fact]
    public async Task RestartFailure_RetriesWholeSequence()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1, MaxRecoveryAttempts = 2 });
        var attempts = 0;
        h.Controller.RestartImpl = _ =>
        {
            attempts++;
            return Task.FromResult(new ContainerOpResult(attempts >= 2, attempts >= 2 ? "ok" : "boom"));
        };

        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Llama, "boom"));

        Assert.Equal(2, attempts);
        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
        Assert.True(h.Gates["llama"].IsOpen);
    }

    [Fact]
    public async Task WarmupFailsEveryAttempt_MarksDown_GatesStayClosedWithReason()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1, MaxRecoveryAttempts = 2 });
        h.Probe.WarmupImpl = _ => Task.FromResult(false);

        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Llama, "boom"));

        Assert.Equal(AiServiceState.Down, h.Monitor.GetState(Llama));
        Assert.Equal(2, h.Controller.RestartedContainers.Count); // retried whole sequence
        Assert.False(h.Gates["llama"].IsOpen);
        Assert.Equal("llama is down: warm-up failed", h.Gates["llama"].CloseReason);
        var down = Assert.IsType<ServiceDown>(h.Events[^1]);
        Assert.Equal("warm-up failed", down.LastError);
    }

    [Fact]
    public async Task ConcurrentFailuresForOneService_StartExactlyOneRecovery()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1 });

        // Park the (single) recovery inside warm-up so it can't complete and flip back to Healthy
        // while the burst is still landing — otherwise a late report would legitimately re-trip.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Probe.WarmupImpl = async _ => { await release.Task; return true; };

        Parallel.For(0, 8, _ => h.Monitor.ReportFailure(Llama, "boom"));

        // Give every queued report a chance to observe the Recovering state.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Controller.RestartedContainers.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(50);

        Assert.Single(h.Events.OfType<RecoveryStarted>());
        Assert.Single(h.Controller.RestartedContainers);

        release.SetResult();
    }

    [Fact]
    public async Task TwoServicesTrippingConcurrently_RecoverSequentially()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1 });

        // Park llama's warm-up inside the exclusive lock until the test releases it.
        var llamaWarmupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Probe.WarmupImpl = async s =>
        {
            if (s.Name == "llama")
            {
                llamaWarmupEntered.TrySetResult();
                await release.Task;
            }
            return true;
        };

        h.Monitor.ReportFailure(Llama, "boom");
        await llamaWarmupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // llama holds the single-flight lock; trip TTS now — it must wait, not restart in parallel.
        h.Monitor.ReportFailure(Tts, "boom");
        await Task.Delay(50);
        Assert.DoesNotContain("read2me-voxcpm2", h.Controller.RestartedContainers);

        release.SetResult();

        // Wait until TTS finishes too.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.Monitor.GetState(Tts) != AiServiceState.Healthy && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Tts));
        Assert.Equal(1, h.Controller.MaxConcurrency); // never two restarts at once
        Assert.Contains("read2me-voxcpm2", h.Controller.RestartedContainers);
    }

    [Fact]
    public void Disabled_ReportFailure_ChangesNothing()
    {
        var h = CreateHarness(new AiWatchdogOptions { Enabled = false, ConsecutiveFailuresToTrip = 1 });

        h.Monitor.ReportFailure(Llama, "boom");

        Assert.Equal(AiServiceState.Untracked, h.Monitor.GetState(Llama)); // no record created
        Assert.Empty(h.Events);
        Assert.True(h.Gates["llama"].IsOpen);
        Assert.Empty(h.Controller.RestartedContainers);
    }

    [Fact]
    public void UnmanagedService_IsUntracked_AndIgnored()
    {
        var h = CreateHarness(mappedServices: new[] { Llama });
        var remote = new DockerAiService("remote", "n/a", "https://api.example.com", "/v1");

        h.Monitor.ReportFailure(remote, "boom");

        Assert.Equal(AiServiceState.Untracked, h.Monitor.GetState(remote));
        Assert.Empty(h.Controller.RestartedContainers);
    }

    [Fact]
    public async Task Reset_ReturnsDownServiceToHealthy_AndOpensGate()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1, MaxRecoveryAttempts = 1 });
        h.Probe.WarmupImpl = _ => Task.FromResult(false);
        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Llama, "boom"));
        Assert.Equal(AiServiceState.Down, h.Monitor.GetState(Llama));

        h.Monitor.Reset(Llama);

        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
        Assert.True(h.Gates["llama"].IsOpen);
    }

    [Fact]
    public async Task AcquireExclusive_SerializesCallers()
    {
        var h = CreateHarness();

        var first = await h.Monitor.AcquireExclusiveAsync(CancellationToken.None);
        var secondTask = h.Monitor.AcquireExclusiveAsync(CancellationToken.None);
        Assert.False(secondTask.IsCompleted); // blocked until first released

        await first.DisposeAsync();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        await second.DisposeAsync();
    }
}
