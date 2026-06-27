using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.Core.Models;
using Read2Me.Services.Events;
using Xunit;

namespace Read2Me.Tests.Services.Characters;

/// Tests for VoiceBatchRunner.RunPhaseAsync<TWork> — the pure envelope.
/// All tests use a FakeSweepPhase; no orchestrator/reader fakes needed.
public class VoiceBatchRunnerTests
{
    private static readonly ProjectFolderId Folder = new("test-book");

    // ── Harness ────────────────────────────────────────────────────────────────

    private static (VoiceBatchRunner Runner, List<VoiceBatchEvent> Events) BuildRunner()
    {
        var broadcaster = new EventBroadcaster<VoiceBatchEvent>();
        var events = new List<VoiceBatchEvent>();
        broadcaster.Event += e => { lock (events) events.Add(e); };
        var runner = new VoiceBatchRunner(NullLogger<VoiceBatchRunner>.Instance, broadcaster);
        return (runner, events);
    }

    private static PhaseDeps DummyDeps() => new(null!, null!, null!);

    // ── Fake phase ─────────────────────────────────────────────────────────────

    private sealed class FakeSweepPhase : ISweepPhase<string>
    {
        private readonly IReadOnlyList<string> _items;
        private readonly Func<string, PhaseStepOutcome> _step;

        public string Operation { get; }

        public FakeSweepPhase(
            IReadOnlyList<string> items,
            Func<string, PhaseStepOutcome>? step = null,
            string operation = "Fake op")
        {
            _items = items;
            _step = step ?? (_ => new PhaseStepOutcome(Ok: true, Update: null, FailReason: null));
            Operation = operation;
        }

        public string DisplayName(string item) => item;

        public Task<IReadOnlyList<string>> PlanAsync(PhaseDeps deps, ProjectFolderId folder, CancellationToken ct)
            => Task.FromResult(_items);

        public Task<PhaseStepOutcome> RunStepAsync(string item, PhaseDeps deps, CancellationToken ct)
            => Task.FromResult(_step(item));
    }

    private sealed class SlowFakeSweepPhase : ISweepPhase<string>
    {
        private readonly IReadOnlyList<string> _items;
        private readonly int _delayMs;

        public string Operation => "Slow op";

        public SlowFakeSweepPhase(IReadOnlyList<string> items, int delayMs)
        {
            _items = items;
            _delayMs = delayMs;
        }

        public string DisplayName(string item) => item;

        public Task<IReadOnlyList<string>> PlanAsync(PhaseDeps deps, ProjectFolderId folder, CancellationToken ct)
            => Task.FromResult(_items);

        public async Task<PhaseStepOutcome> RunStepAsync(string item, PhaseDeps deps, CancellationToken ct)
        {
            await Task.Delay(_delayMs, ct);
            return new PhaseStepOutcome(Ok: true, Update: null, FailReason: null);
        }
    }

    // ── Test 1: counter math ───────────────────────────────────────────────────

    [Fact]
    public async Task RunPhaseAsync_AllSucceed_CountersCorrectAndBatchCompletedPublished()
    {
        var (runner, events) = BuildRunner();
        var phase = new FakeSweepPhase(new[] { "a", "b", "c" });

        await runner.RunPhaseAsync(phase, DummyDeps(), Folder, CancellationToken.None);

        Assert.Equal(3, runner.Total);
        Assert.Equal(3, runner.Processed);
        Assert.Equal(0, runner.Failed);
        Assert.False(runner.IsRunning);
        Assert.Contains(events, e => e is BatchCompleted bc && bc.Processed == 3 && bc.Failed == 0);
    }

    // ── Test 2: cancel mid-sweep ───────────────────────────────────────────────

    [Fact]
    public async Task RunPhaseAsync_CancelMidSweep_StopsAndPublishesBatchCancelled()
    {
        var (runner, events) = BuildRunner();
        using var cts = new CancellationTokenSource();
        int callCount = 0;

        var phase = new FakeSweepPhase(
            items: new[] { "a", "b", "c", "d", "e" },
            step: item =>
            {
                callCount++;
                if (callCount == 2) cts.Cancel();
                return new PhaseStepOutcome(Ok: true, Update: null, FailReason: null);
            });

        await runner.RunPhaseAsync(phase, DummyDeps(), Folder, cts.Token);

        Assert.False(runner.IsRunning);
        Assert.Contains(events, e => e is BatchCancelled);
        Assert.DoesNotContain(events, e => e is BatchCompleted);
        Assert.True(runner.Processed < 5);
    }

    // ── Test 3: soft-fail continue ────────────────────────────────────────────

    [Fact]
    public async Task RunPhaseAsync_SoftFailOnOneItem_FailedIncrementedAndSweepContinues()
    {
        var (runner, events) = BuildRunner();
        var phase = new FakeSweepPhase(
            items: new[] { "a", "b", "c" },
            step: item => item == "b"
                ? new PhaseStepOutcome(Ok: false, Update: null, FailReason: "bad")
                : new PhaseStepOutcome(Ok: true, Update: null, FailReason: null));

        await runner.RunPhaseAsync(phase, DummyDeps(), Folder, CancellationToken.None);

        Assert.Equal(3, runner.Processed);
        Assert.Equal(1, runner.Failed);
        Assert.Contains(events, e => e is BatchCompleted bc && bc.Processed == 2 && bc.Failed == 1);
    }

    // ── Test 4: exception continue ────────────────────────────────────────────

    [Fact]
    public async Task RunPhaseAsync_StepThrowsNonOce_FailedIncrementedAndSweepContinues()
    {
        var (runner, events) = BuildRunner();
        var phase = new FakeSweepPhase(
            items: new[] { "a", "b", "c" },
            step: item =>
            {
                if (item == "a") throw new InvalidOperationException("boom");
                return new PhaseStepOutcome(Ok: true, Update: null, FailReason: null);
            });

        await runner.RunPhaseAsync(phase, DummyDeps(), Folder, CancellationToken.None);

        Assert.Equal(3, runner.Processed);
        Assert.Equal(1, runner.Failed);
        Assert.Contains(events, e => e is BatchCompleted bc && bc.Processed == 2 && bc.Failed == 1);
    }

    // ── Test 5: event order ────────────────────────────────────────────────────

    [Fact]
    public async Task RunPhaseAsync_TwoItems_EventOrderIsCorrect()
    {
        var (runner, events) = BuildRunner();
        var updateA = new VoiceUpdated(Guid.NewGuid(), Guid.NewGuid(), "promptA", null, null);
        var updateB = new VoiceUpdated(Guid.NewGuid(), Guid.NewGuid(), "promptB", null, null);

        var phase = new FakeSweepPhase(
            items: new[] { "a", "b" },
            step: item => item == "a"
                ? new PhaseStepOutcome(Ok: true, Update: updateA, FailReason: null)
                : new PhaseStepOutcome(Ok: true, Update: updateB, FailReason: null));

        await runner.RunPhaseAsync(phase, DummyDeps(), Folder, CancellationToken.None);

        Assert.IsType<BatchStarted>(events[0]);
        Assert.IsType<BatchProgress>(events[1]);
        Assert.IsType<VoiceUpdated>(events[2]);
        Assert.IsType<BatchProgress>(events[3]);
        Assert.IsType<VoiceUpdated>(events[4]);
        Assert.IsType<BatchCompleted>(events[5]);
        Assert.Equal(6, events.Count);
    }
}
