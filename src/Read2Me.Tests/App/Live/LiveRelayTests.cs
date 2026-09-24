using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Read2Me.App.Characters;
using Read2Me.App.Live;
using Read2Me.App.State;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;
using Xunit;

namespace Read2Me.Tests.App.Live;

/// <summary>
/// The relay against real singletons and a recording hub. Timing assertions use real time with
/// generous bounds: the coalescers' exact arithmetic is covered in <see cref="CoalescerTests"/>.
/// </summary>
public class LiveRelayTests : IAsyncLifetime
{
    private readonly RecordingHubContext _hub = new();
    private readonly LiveConnectionRegistry _registry = new();
    private readonly EventBroadcaster<LlmStreamEvent> _llm = new();
    private readonly EventBroadcaster<AudioGenEvent> _audioGen = new();
    private readonly EventBroadcaster<AssemblyEvent> _assembly = new();
    private readonly EventBroadcaster<VoiceBatchEvent> _voiceBatch = new();
    private readonly EventBroadcaster<WatchdogEvent> _watchdog = new();
    private readonly EventBroadcaster<ServiceStatusChanged> _serviceStatus = new();
    private readonly EventBroadcaster<BookMutationReceipt> _receipts = new();
    private readonly EventBroadcaster<SettingsChanged> _settings = new();
    private readonly CharacterQueueService _attribution = new();
    private readonly AudioQueueService _audio = new();
    private readonly NodeStatusService _nodes;
    private readonly AudioReviewService _reviews = new();
    private LiveRelay _relay = null!;

    public LiveRelayTests() => _nodes = new NodeStatusService(_attribution);

    public async ValueTask InitializeAsync()
    {
        var progress = new AttributionProgressState(_llm, _attribution);
        var assemblyService = new AudiobookAssemblyService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IAudiobookEncoder>(), _assembly, Substitute.For<IFileSystem>(),
            NullLogger<AudiobookAssemblyService>.Instance);
        var batchRunner = new VoiceBatchRunner(NullLogger<VoiceBatchRunner>.Instance, _voiceBatch, _llm);
        var throughput = new ThroughputAggregator(_llm, new EventBroadcaster<LlmTimingsSample>());
        var status = new ProjectStatusSource(_nodes, _attribution, _audio, _reviews, new BookRevisionSequence());

        _relay = new LiveRelay(_hub, _registry, status, _llm, _audioGen, _assembly, _voiceBatch, _watchdog, _serviceStatus, _receipts,
            _settings, _attribution, _audio, _nodes, _reviews, progress, assemblyService, batchRunner, throughput,
            NullLogger<LiveRelay>.Instance);
        await _relay.StartAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _hub.Gate?.TrySetResult();
        await _relay.StopAsync(CancellationToken.None);
        _relay.Dispose();
    }

    [Fact]
    public async Task Service_status_observations_and_watchdog_transitions_reach_global_and_the_snapshot()
    {
        _serviceStatus.Publish(new ServiceStatusChanged("llama", AiServiceStatus.Stopped, "shutdown", true, null));
        _watchdog.Publish(new ServiceDown("whisper", "gave up"));

        await WaitForAsync(() => _hub.Method("serviceStatus").Count == 2);

        var sent = _hub.Method("serviceStatus");
        Assert.All(sent, s => Assert.Equal("global", s.Target));
        var op = Assert.IsType<ServiceStatusMessage>(sent[0].Payload);
        Assert.Equal(new ServiceStatusMessage("llama", "Stopped", "shutdown", true, null), op);
        var implied = Assert.IsType<ServiceStatusMessage>(sent[1].Payload);
        Assert.Equal(new ServiceStatusMessage("whisper", "Down"), implied);

        var snapshot = _relay.BuildSnapshot([]);
        Assert.Equal("Stopped", snapshot.ServiceStatus["llama"]);
        Assert.Equal("Down", snapshot.ServiceStatus["whisper"]);
        Assert.Equal("serviceDown", snapshot.Watchdog["whisper"]);
    }

    [Fact]
    public async Task Receipt_goes_only_to_its_project_group_with_origin_id_untouched()
    {
        var origin = Guid.NewGuid();
        _receipts.Publish(new BookMutationReceipt(new ProjectFolderId("folder-a"), "CreateCharacter", Guid.NewGuid(), 7,
            BookMutationEffects.Unknown) { OriginId = origin });

        await WaitForAsync(() => _hub.Method("receipt").Count == 1);

        var sent = Assert.Single(_hub.Method("receipt"));
        Assert.Equal("project:folder-a", sent.Target);
        var receipt = Assert.IsType<ReceiptMessage>(sent.Payload);
        Assert.Equal(origin, receipt.OriginId);
        Assert.Equal(7, receipt.Revision);
        Assert.Equal("folder-a", receipt.Folder);
    }

    [Fact]
    public async Task Queue_pulses_flood_becomes_debounced_snapshots_first_one_within_300ms()
    {
        var folder = new ProjectFolderId("flood");
        var started = Stopwatch.StartNew();
        var flood = Task.Run(async () =>
        {
            var until = Stopwatch.StartNew();
            while (until.ElapsedMilliseconds < 1000)
            {
                _attribution.Enqueue([Paragraph(folder)]);
                await Task.Delay(2);
            }
        });

        await WaitForAsync(() => _hub.Method("queue").Count >= 1, timeoutMs: 300);
        var firstAt = started.ElapsedMilliseconds;
        await flood;
        await Task.Delay(300); // let the trailing window close

        var queues = _hub.Method("queue");
        Assert.True(firstAt <= 300, $"first queue message took {firstAt} ms");
        Assert.InRange(queues.Count, 1, 6); // ~1.3 s of pulses at ≤ 4/s
        var message = Assert.IsType<QueueMessage>(queues[^1].Payload);
        Assert.True(message.Attribution.QueuedCount > 0);
        Assert.All(queues, q => Assert.Equal(LiveGroups.Global, q.Target));
    }

    [Fact]
    public async Task Llm_deltas_are_batched_into_one_message_and_flushed_before_a_control_event()
    {
        _llm.Publish(new RequestStarted("p", "prompt", 1, "cfg"));
        for (var i = 0; i < 50; i++) _llm.Publish(new ThinkingDelta("t"));
        for (var i = 0; i < 50; i++) _llm.Publish(new ContentDelta("c"));
        _llm.Publish(new StreamCompleted(1, 100, 10, 10));

        await WaitForAsync(() => _hub.Method("llm").Count == 3);

        var llm = _hub.Method("llm").Select(m => (LlmMessage)m.Payload).ToList();
        Assert.Equal(["requestStarted", "delta", "streamCompleted"], llm.Select(m => m.Kind));
        Assert.Equal(new string('t', 50), llm[1].Thinking);
        Assert.Equal(new string('c', 50), llm[1].Content);
        Assert.All(_hub.Method("llm"), m => Assert.Equal(LiveGroups.StreamLlm, m.Target));
    }

    [Fact]
    public async Task Publish_returns_immediately_while_the_drain_loop_is_blocked()
    {
        _hub.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watchdog.Publish(new ServiceHealthy("llama")); // first send parks the drain loop on the gate
        await WaitForAsync(() => _hub.Method("watchdog").Count == 1);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
            _llm.Publish(new ContentDelta("x"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"20k publishes took {sw.ElapsedMilliseconds} ms with the drain blocked");
        Assert.Single(_hub.Method("watchdog")); // nothing else got through while blocked
        Assert.True(_relay.Dropped > 0, "the bounded channel should have shed the oldest deltas rather than block");
    }

    [Fact]
    public async Task Status_deltas_go_to_joined_project_groups_only_and_only_when_something_changed()
    {
        var a = new ProjectFolderId("proj-a");
        var b = new ProjectFolderId("proj-b");
        var chapter = Guid.NewGuid();
        var part = Guid.NewGuid();
        var volume = Guid.NewGuid();
        var paragraph = Guid.NewGuid();
        _registry.Connected("c1");
        _registry.JoinProject("c1", a.Value);

        _nodes.Seed(a, [new ParagraphStatusSeedRow(paragraph, chapter, part, volume, 1, 1, 0)]);
        _nodes.Seed(b, [new ParagraphStatusSeedRow(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0, 0)]);
        _attribution.Enqueue([new QueuedParagraph(a, paragraph, "p", chapter, part, volume)]);
        _reviews.Set(a, Guid.NewGuid(), new AudioReviewInfo(AudioReviewState.NeedsReview, true, null, false, 0.4, "wer", "t", "o"));

        await WaitForAsync(() => _hub.Method("nodeStatus").Count >= 1 && _hub.Method("itemStatus").Count >= 1);
        await Task.Delay(400); // a second window with nothing new must stay silent

        Assert.All(_hub.Method("nodeStatus").Concat(_hub.Method("itemStatus")), m => Assert.Equal("project:proj-a", m.Target));
        var nodeStatus = Assert.IsType<NodeStatusMessage>(Assert.Single(_hub.Method("nodeStatus")).Payload);
        Assert.Equal(3, nodeStatus.Nodes.Count);
        Assert.Equal(1, nodeStatus.FolderAudioRemaining);
        Assert.Equal(1, nodeStatus.Nodes[chapter.ToString()]!.Value.AttributionQueued);
        var itemStatus = Assert.IsType<ItemStatusMessage>(Assert.Single(_hub.Method("itemStatus")).Payload);
        Assert.Equal(ParagraphQueueStatus.Queued, itemStatus.Paragraphs[paragraph.ToString()]!.Status);
        Assert.Single(itemStatus.Items);
        Assert.NotNull(itemStatus.Items.Values.Single()!.Review);
    }

    [Fact]
    public async Task Encode_progress_is_stepped_and_other_assembly_events_pass_through()
    {
        _assembly.Publish(new AssemblyPhaseStarted(AssemblyPhase.Encode));
        for (var i = 0; i <= 90; i++) _assembly.Publish(new AssemblyEncodeProgress(i / 10_000.0)); // 0 → 0.9 %
        _assembly.Publish(new AssemblyEncodeProgress(0.5));
        _assembly.Publish(new AssemblyCompleted("Dune.m4b") { Folder = "dune" });

        await WaitForAsync(() => _hub.Method("assembly").Any(m => ((AssemblyMessage)m.Payload).Kind == "completed"));

        var kinds = _hub.Method("assembly").Select(m => (AssemblyMessage)m.Payload).ToList();
        Assert.Equal(["phaseStarted", "progress", "progress", "completed"], kinds.Select(k => k.Kind));
        Assert.Equal(0.0, kinds[1].Fraction);
        Assert.Equal(0.5, kinds[2].Fraction);
        Assert.Equal("Encode", kinds[0].Phase);
        Assert.Equal("Dune.m4b", kinds[3].OutputFileName);
        Assert.Equal("dune", kinds[3].Folder);
    }

    [Fact]
    public async Task Settings_change_and_watchdog_reach_the_global_group_and_watchdog_state_is_remembered()
    {
        _settings.Publish(new SettingsChanged(SettingsArea.Llm));
        _watchdog.Publish(new RecoveryStarted("llama", "wedged"));

        await WaitForAsync(() => _hub.Method("settingsChanged").Count == 1 && _hub.Method("watchdog").Count == 1);

        Assert.Equal("llm", ((SettingsChangedMessage)_hub.Method("settingsChanged")[0].Payload).Area);
        var snapshot = _relay.BuildSnapshot([]);
        Assert.Equal("recoveryStarted", snapshot.Watchdog["llama"]);
        Assert.False(snapshot.Assembly.IsRunning);
        Assert.Empty(snapshot.Projects);
    }

    private static QueuedParagraph Paragraph(ProjectFolderId folder) =>
        new(folder, Guid.NewGuid(), "p", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Condition not met within {timeoutMs} ms");
            await Task.Delay(10);
        }
    }
}
