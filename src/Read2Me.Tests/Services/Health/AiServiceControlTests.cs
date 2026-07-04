using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Xunit;

namespace Read2Me.Tests.Services.Health;

public class AiServiceControlTests
{
    private static readonly DockerAiService Llama = new("llama", "read2me-llama", "http://localhost:8080", "/health");

    // ---- Fakes ------------------------------------------------------------

    private sealed class FakeGate : IWatchdogGate
    {
        private readonly List<string> _log;
        public FakeGate(List<string> log) => _log = log;

        public bool IsOpen { get; private set; } = true;
        public string? CloseReason { get; private set; }
        public bool HasPendingWork { get; set; }
        public int CloseCount { get; private set; }
        public int OpenCount { get; private set; }

        public void Close(string reason)
        {
            if (!IsOpen) return;
            IsOpen = false;
            CloseReason = reason;
            CloseCount++;
            _log.Add("close");
        }

        public void Open()
        {
            IsOpen = true;
            CloseReason = null;
            OpenCount++;
            _log.Add("open");
        }
    }

    private sealed class FakeController : IContainerController
    {
        private readonly List<string> _log;
        public FakeController(List<string> log) => _log = log;

        public ContainerOpResult StartResult { get; set; } = new(true, "ok");
        public ContainerOpResult RestartResult { get; set; } = new(true, "ok");
        public ContainerOpResult StopResult { get; set; } = new(true, "ok");
        public ContainerRunState State { get; set; } = ContainerRunState.Running;
        public List<string> StateQueries { get; } = new();

        public Task<ContainerOpResult> StartAsync(string c, CancellationToken ct)
        {
            _log.Add("start");
            return Task.FromResult(StartResult);
        }

        public Task<ContainerOpResult> RestartAsync(string c, CancellationToken ct)
        {
            _log.Add("restart");
            return Task.FromResult(RestartResult);
        }

        public Task<ContainerOpResult> StopAsync(string c, CancellationToken ct)
        {
            _log.Add("stop");
            return Task.FromResult(StopResult);
        }

        public Task<ContainerRunState> GetStateAsync(string c, CancellationToken ct)
        {
            StateQueries.Add(c);
            return Task.FromResult(State);
        }
    }

    private sealed class FakeProbe : IAiServiceProbe
    {
        private readonly List<string> _log;
        public FakeProbe(List<string> log) => _log = log;

        public bool WaitHealthyResult { get; set; } = true;
        public bool IsHealthyResult { get; set; } = true;
        public bool WarmupResult { get; set; } = true;

        public Task<bool> WaitUntilHealthyAsync(DockerAiService service, CancellationToken ct)
        {
            _log.Add("health");
            return Task.FromResult(WaitHealthyResult);
        }

        public Task<bool> IsHealthyAsync(DockerAiService service, CancellationToken ct) =>
            Task.FromResult(IsHealthyResult);

        public Task<bool> WarmupAsync(DockerAiService service, CancellationToken ct)
        {
            _log.Add("warmup");
            return Task.FromResult(WarmupResult);
        }
    }

    // ---- Harness ----------------------------------------------------------

    private sealed class Harness
    {
        public required AiServiceControl Control { get; init; }
        public required AiServiceHealthMonitor Monitor { get; init; }
        public required FakeController Controller { get; init; }
        public required FakeProbe Probe { get; init; }
        public required FakeGate Gate { get; init; }
        public required List<string> OrderLog { get; init; }
        public required EventBroadcaster<WatchdogEvent> Broadcaster { get; init; }

        /// <summary>Trips the monitor and waits for the terminal (Healthy/Down) recovery event.</summary>
        public async Task AwaitRecovery(Action trigger)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(WatchdogEvent e)
            {
                if (e is ServiceHealthy or ServiceDown) done.TrySetResult();
            }
            Broadcaster.Event += Handler;
            try
            {
                trigger();
                await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Broadcaster.Event -= Handler;
            }
        }
    }

    private static Harness CreateHarness(AiWatchdogOptions? options = null)
    {
        var log = new List<string>();
        var controller = new FakeController(log);
        var probe = new FakeProbe(log);
        var gate = new FakeGate(log);
        var map = new WatchdogGateMap(new Dictionary<string, IReadOnlyList<IWatchdogGate>>(StringComparer.OrdinalIgnoreCase)
        {
            [Llama.Name] = new IWatchdogGate[] { gate },
        });
        var broadcaster = new EventBroadcaster<WatchdogEvent>();
        var opts = Options.Create(options ?? new AiWatchdogOptions());

        var monitor = new AiServiceHealthMonitor(
            controller, probe, map, opts, broadcaster, NullLogger<AiServiceHealthMonitor>.Instance);
        var control = new AiServiceControl(
            new DockerAiServiceRegistry(), controller, probe, map, monitor, NullLogger<AiServiceControl>.Instance);

        return new Harness
        {
            Control = control,
            Monitor = monitor,
            Controller = controller,
            Probe = probe,
            Gate = gate,
            OrderLog = log,
            Broadcaster = broadcaster,
        };
    }

    // ---- Resolve ----------------------------------------------------------

    [Fact]
    public void Resolve_ReturnsEntryForDockerUrl_NullForRemote()
    {
        var h = CreateHarness();

        var docker = h.Control.Resolve("http://localhost:8080");
        var remote = h.Control.Resolve("https://api.openai.com");

        Assert.NotNull(docker);
        Assert.Equal("llama", docker!.Name);
        Assert.Null(remote);
    }

    // ---- GetStatus --------------------------------------------------------

    [Fact]
    public async Task GetStatus_MonitorRecovering_ShortCircuits_WithoutTouchingContainer()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1 });
        // Hold the exclusive lock so recovery cannot progress past acquiring it — ReportFailure flips
        // the state to Recovering synchronously before the background recovery task runs.
        var hold = await h.Monitor.AcquireExclusiveAsync(CancellationToken.None);
        try
        {
            h.Monitor.ReportFailure(Llama, "boom"); // trips → state Recovering immediately
            Assert.Equal(AiServiceState.Recovering, h.Monitor.GetState(Llama));

            var status = await h.Control.GetStatusAsync(Llama, CancellationToken.None);

            Assert.Equal(AiServiceStatus.Recovering, status);
            Assert.Empty(h.Controller.StateQueries); // short-circuit — no container lookup
        }
        finally
        {
            await hold.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetStatus_MonitorDown_ShortCircuits()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 1, MaxRecoveryAttempts = 1 });
        h.Probe.WarmupResult = false; // recovery gives up → Down
        await h.AwaitRecovery(() => h.Monitor.ReportFailure(Llama, "boom"));
        Assert.Equal(AiServiceState.Down, h.Monitor.GetState(Llama));

        var status = await h.Control.GetStatusAsync(Llama, CancellationToken.None);

        Assert.Equal(AiServiceStatus.Down, status);
    }

    [Theory]
    [InlineData(ContainerRunState.NotFound, AiServiceStatus.NotFound)]
    [InlineData(ContainerRunState.Stopped, AiServiceStatus.Stopped)]
    [InlineData(ContainerRunState.Unknown, AiServiceStatus.Unknown)]
    public async Task GetStatus_ContainerState_PassesThrough(ContainerRunState run, AiServiceStatus expected)
    {
        var h = CreateHarness();
        h.Controller.State = run;

        Assert.Equal(expected, await h.Control.GetStatusAsync(Llama, CancellationToken.None));
    }

    [Fact]
    public async Task GetStatus_RunningAndHealthy_IsReady()
    {
        var h = CreateHarness();
        h.Controller.State = ContainerRunState.Running;
        h.Probe.IsHealthyResult = true;

        Assert.Equal(AiServiceStatus.Ready, await h.Control.GetStatusAsync(Llama, CancellationToken.None));
    }

    [Fact]
    public async Task GetStatus_RunningButNotAnswering_IsStarting()
    {
        var h = CreateHarness();
        h.Controller.State = ContainerRunState.Running;
        h.Probe.IsHealthyResult = false;

        Assert.Equal(AiServiceStatus.Starting, await h.Control.GetStatusAsync(Llama, CancellationToken.None));
    }

    [Fact]
    public async Task GetStatus_TouchesOnlyRequestedContainer()
    {
        var h = CreateHarness();

        await h.Control.GetStatusAsync(Llama, CancellationToken.None);

        Assert.Equal(new[] { "read2me-llama" }, h.Controller.StateQueries);
    }

    // ---- Start / Restart --------------------------------------------------

    [Fact]
    public async Task Start_RunsSequence_ThenReadyAndResets()
    {
        var h = CreateHarness();
        h.Gate.Close("was closed"); // simulate a gate held from a prior Down

        var result = await h.Control.StartAsync(Llama, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AiServiceStatus.Ready, result.Status);
        Assert.Null(result.Error);
        // start → health poll → warm-up → reset(open).
        Assert.Equal(new[] { "close", "start", "health", "warmup", "open" }, h.OrderLog);
        Assert.True(h.Gate.IsOpen); // Reset reopened it
        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
    }

    [Fact]
    public async Task Restart_RunsSequenceWithRestart()
    {
        var h = CreateHarness();

        var result = await h.Control.RestartAsync(Llama, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "restart", "health", "warmup", "open" }, h.OrderLog);
    }

    [Fact]
    public async Task Start_HealthPollTimeout_FailsWithoutReset()
    {
        var h = CreateHarness();
        h.Probe.WaitHealthyResult = false;
        h.Controller.State = ContainerRunState.Running; // failure status probe
        h.Probe.IsHealthyResult = false;

        var result = await h.Control.StartAsync(Llama, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("health check timed out", result.Error);
        Assert.DoesNotContain("warmup", h.OrderLog);
        Assert.Equal(0, h.Gate.OpenCount); // no Reset
    }

    [Fact]
    public async Task Start_WarmupFailure_FailsWithoutReset()
    {
        var h = CreateHarness();
        h.Probe.WarmupResult = false;

        var result = await h.Control.StartAsync(Llama, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("warm-up failed", result.Error);
        Assert.Equal(0, h.Gate.OpenCount);
    }

    [Fact]
    public async Task Start_DockerStartFails_FailsWithoutReset()
    {
        var h = CreateHarness();
        h.Controller.StartResult = new ContainerOpResult(false, "boom");

        var result = await h.Control.StartAsync(Llama, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("boom", result.Error);
        Assert.DoesNotContain("health", h.OrderLog);
        Assert.Equal(0, h.Gate.OpenCount);
    }

    [Fact]
    public async Task ManualOp_WhileExclusiveLockHeld_Waits()
    {
        var h = CreateHarness();
        var hold = await h.Monitor.AcquireExclusiveAsync(CancellationToken.None);

        var startTask = h.Control.StartAsync(Llama, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(startTask.IsCompleted);       // blocked on the lock
        Assert.DoesNotContain("start", h.OrderLog); // never ran a lifecycle op concurrently

        await hold.DisposeAsync();
        var result = await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Succeeded);
    }

    // ---- Shutdown ---------------------------------------------------------

    [Fact]
    public async Task Shutdown_WithPendingWork_StopsAndClosesGateWithReason()
    {
        var h = CreateHarness();
        h.Gate.HasPendingWork = true;

        var result = await h.Control.ShutdownAsync(Llama, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AiServiceStatus.Stopped, result.Status);
        Assert.Contains("stop", h.OrderLog);
        Assert.False(h.Gate.IsOpen);
        Assert.Equal("llama was shut down", h.Gate.CloseReason);
    }

    [Fact]
    public async Task Shutdown_IdleQueue_StopsQuietly()
    {
        var h = CreateHarness();
        h.Gate.HasPendingWork = false;

        var result = await h.Control.ShutdownAsync(Llama, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(h.Gate.IsOpen);      // never closed
        Assert.Equal(0, h.Gate.CloseCount);
    }

    [Fact]
    public async Task Shutdown_ClearsFailureCounter()
    {
        var h = CreateHarness(new AiWatchdogOptions { ConsecutiveFailuresToTrip = 2 });
        h.Monitor.ReportFailure(Llama, "one"); // one of two — a lingering streak

        await h.Control.ShutdownAsync(Llama, CancellationToken.None);
        // A fresh failure after shutdown must be the first of a new pair, not the trip.
        h.Monitor.ReportFailure(Llama, "two");

        Assert.Equal(AiServiceState.Healthy, h.Monitor.GetState(Llama));
        Assert.Empty(h.Controller.StateQueries); // no recovery restart kicked off
    }

    [Fact]
    public async Task Shutdown_DockerStopFails_ReturnsError()
    {
        var h = CreateHarness();
        h.Gate.HasPendingWork = true;
        h.Controller.StopResult = new ContainerOpResult(false, "nope");

        var result = await h.Control.ShutdownAsync(Llama, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("nope", result.Error);
        Assert.True(h.Gate.IsOpen); // stop failed → gate untouched
    }
}
