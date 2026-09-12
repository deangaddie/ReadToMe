using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Read2Me.App.Characters;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.Live;

/// <summary>
/// The one bridge from in-process events to hub clients. Subscribes to every singleton event
/// source in its constructor (so, resolved eagerly at startup like the journals, it misses
/// nothing) and does <em>no work on the publisher's thread beyond a channel write</em>: a single
/// drain loop applies the relay policy (research/live-events.md §8) and performs the sends.
/// <list type="bullet">
/// <item>Pass-through: receipts (project group), watchdog, assembly phases, voice batch control,
/// LLM control events and every audio-gen phase (stream groups), settings changes.</item>
/// <item><see cref="DeltaBatcher"/>: LLM thinking/content text, one message per 100 ms.</item>
/// <item><see cref="Debouncer"/>: queue roll-ups and per-project node/item status, 250 ms.</item>
/// <item><see cref="ProgressStepper"/>: encode progress at ≥ 1 % steps.</item>
/// <item><see cref="ThroughputTicker"/>: one throughput snapshot per second while a run is active.</item>
/// </list>
/// Nothing here consumes a receipt to reconcile Book state (ADR 0007): the relay forwards it
/// verbatim, and the Angular client reimplements the projection from receipts.
/// </summary>
public sealed class LiveRelay : IHostedService, IDisposable
{
    private abstract record Item;
    private sealed record LlmItem(LlmStreamEvent Event) : Item;
    private sealed record AudioGenItem(AudioGenEvent Event) : Item;
    private sealed record AssemblyItem(AssemblyEvent Event) : Item;
    private sealed record VoiceBatchItem(VoiceBatchEvent Event) : Item;
    private sealed record WatchdogItem(WatchdogEvent Event) : Item;
    private sealed record ReceiptItem(BookMutationReceipt Receipt) : Item;
    private sealed record SettingsItem(SettingsChanged Change) : Item;
    private sealed record QueuePulse : Item;
    private sealed record StatusPulse : Item;

    private sealed record LastSent(
        IReadOnlyDictionary<string, NodeStatusSummary?> Nodes,
        int FolderAudioRemaining,
        IReadOnlyDictionary<string, ParagraphStatusEntry?> Paragraphs,
        IReadOnlyDictionary<string, ItemStatusEntry?> Items);

    public const int ChannelCapacity = 8192;

    private readonly IHubContext<LiveHub, ILiveClient> _hub;
    private readonly LiveConnectionRegistry _registry;
    private readonly ProjectStatusSource _status;
    private readonly EventBroadcaster<LlmStreamEvent> _llm;
    private readonly EventBroadcaster<AudioGenEvent> _audioGen;
    private readonly EventBroadcaster<AssemblyEvent> _assembly;
    private readonly EventBroadcaster<VoiceBatchEvent> _voiceBatch;
    private readonly EventBroadcaster<WatchdogEvent> _watchdog;
    private readonly EventBroadcaster<BookMutationReceipt> _receipts;
    private readonly EventBroadcaster<SettingsChanged> _settings;
    private readonly CharacterQueueService _attributionQueue;
    private readonly AudioQueueService _audioQueue;
    private readonly NodeStatusService _nodeStatus;
    private readonly AudioReviewService _reviews;
    private readonly AttributionProgressState _progress;
    private readonly AudiobookAssemblyService _assemblyService;
    private readonly VoiceBatchRunner _batchRunner;
    private readonly ThroughputAggregator _throughput;
    private readonly ILogger<LiveRelay> _logger;
    private readonly TimeProvider _clock;

    private readonly Channel<Item> _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(ChannelCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    private readonly DeltaBatcher _deltas = new();
    private readonly Debouncer _queueDebounce = new();
    private readonly Debouncer _statusDebounce = new();
    private readonly Debouncer _batchProgressDebounce = new();
    private readonly ProgressStepper _encodeSteps = new();
    private readonly ThroughputTicker _throughputTicker = new();
    private VoiceBatchMessage? _pendingBatchProgress;
    private readonly Dictionary<string, LastSent> _lastSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _watchdogLast = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Task? _drain;
    private Task<bool>? _pendingWait;
    private long _written;
    private long _handled;

    public LiveRelay(
        IHubContext<LiveHub, ILiveClient> hub,
        LiveConnectionRegistry registry,
        ProjectStatusSource status,
        EventBroadcaster<LlmStreamEvent> llm,
        EventBroadcaster<AudioGenEvent> audioGen,
        EventBroadcaster<AssemblyEvent> assembly,
        EventBroadcaster<VoiceBatchEvent> voiceBatch,
        EventBroadcaster<WatchdogEvent> watchdog,
        EventBroadcaster<BookMutationReceipt> receipts,
        EventBroadcaster<SettingsChanged> settings,
        CharacterQueueService attributionQueue,
        AudioQueueService audioQueue,
        NodeStatusService nodeStatus,
        AudioReviewService reviews,
        AttributionProgressState progress,
        AudiobookAssemblyService assemblyService,
        VoiceBatchRunner batchRunner,
        ThroughputAggregator throughput,
        ILogger<LiveRelay> logger,
        TimeProvider? clock = null)
    {
        _hub = hub;
        _registry = registry;
        _status = status;
        _llm = llm;
        _audioGen = audioGen;
        _assembly = assembly;
        _voiceBatch = voiceBatch;
        _watchdog = watchdog;
        _receipts = receipts;
        _settings = settings;
        _attributionQueue = attributionQueue;
        _audioQueue = audioQueue;
        _nodeStatus = nodeStatus;
        _reviews = reviews;
        _progress = progress;
        _assemblyService = assemblyService;
        _batchRunner = batchRunner;
        _throughput = throughput;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;

        _llm.Event += OnLlm;
        _audioGen.Event += OnAudioGen;
        _assembly.Event += OnAssembly;
        _voiceBatch.Event += OnVoiceBatch;
        _watchdog.Event += OnWatchdog;
        _receipts.Event += OnReceipt;
        _settings.Event += OnSettings;
        _attributionQueue.Changed += OnQueueChanged;
        _audioQueue.Changed += OnQueueChanged;
        _nodeStatus.Changed += OnStatusChanged;
        _reviews.Changed += OnStatusChanged;
        _progress.Changed += OnProgressChanged;
    }

    /// <summary>
    /// Items shed because the channel was full (DropOldest, so TryWrite itself never fails): written
    /// minus handled minus still buffered. Zero in normal operation; a diagnostic, not a contract.
    /// </summary>
    public long Dropped => Math.Max(0, Interlocked.Read(ref _written) - Interlocked.Read(ref _handled) - _channel.Reader.Count);

    // ---- publisher-thread handlers: one TryWrite each, never a send -------------------------------

    private void OnLlm(LlmStreamEvent e) => Post(new LlmItem(e));
    private void OnAudioGen(AudioGenEvent e) => Post(new AudioGenItem(e));
    private void OnAssembly(AssemblyEvent e) => Post(new AssemblyItem(e));
    private void OnVoiceBatch(VoiceBatchEvent e) => Post(new VoiceBatchItem(e));
    private void OnWatchdog(WatchdogEvent e) => Post(new WatchdogItem(e));
    private void OnReceipt(BookMutationReceipt r) => Post(new ReceiptItem(r));
    private void OnSettings(SettingsChanged c) => Post(new SettingsItem(c));
    private void OnProgressChanged() => Post(new QueuePulse());

    // A queue moving changes both the roll-up and per-item status.
    private void OnQueueChanged()
    {
        Post(new QueuePulse());
        Post(new StatusPulse());
    }

    private void OnStatusChanged() => Post(new StatusPulse());

    private void Post(Item item)
    {
        Interlocked.Increment(ref _written);
        _channel.Writer.TryWrite(item);
    }

    // ---- hosting -----------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _drain = Task.Run(() => DrainAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _cts?.Cancel();
        if (_drain is not null)
        {
            try { await _drain.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Idempotent: the relay is registered as a singleton and as a hosted service, so DI disposes it twice.</summary>
    public void Dispose()
    {
        Unsubscribe();
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private void Unsubscribe()
    {
        _llm.Event -= OnLlm;
        _audioGen.Event -= OnAudioGen;
        _assembly.Event -= OnAssembly;
        _voiceBatch.Event -= OnVoiceBatch;
        _watchdog.Event -= OnWatchdog;
        _receipts.Event -= OnReceipt;
        _settings.Event -= OnSettings;
        _attributionQueue.Changed -= OnQueueChanged;
        _audioQueue.Changed -= OnQueueChanged;
        _nodeStatus.Changed -= OnStatusChanged;
        _reviews.Changed -= OnStatusChanged;
        _progress.Changed -= OnProgressChanged;
    }

    // ---- drain loop --------------------------------------------------------------------------------

    private async Task DrainAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = _clock.GetUtcNow();
                var due = NextDue();
                if (due is { } dueNow && dueNow <= now)
                {
                    await FlushDueAsync(now);
                    continue;
                }

                _pendingWait ??= reader.WaitToReadAsync(ct).AsTask();
                if (due is { } later)
                    await Task.WhenAny(_pendingWait, Task.Delay(later - now, _clock, ct));
                else
                    await _pendingWait;

                if (_pendingWait.IsCompleted)
                {
                    _pendingWait = null;
                    while (reader.TryRead(out var item))
                    {
                        Interlocked.Increment(ref _handled);
                        await HandleAsync(item, _clock.GetUtcNow());
                    }
                }
                await FlushDueAsync(_clock.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live relay drain loop stopped unexpectedly; hub clients will no longer receive pushes.");
        }
    }

    private DateTimeOffset? NextDue()
    {
        DateTimeOffset? due = null;
        void Consider(DateTimeOffset? candidate)
        {
            if (candidate is { } c && (due is null || c < due)) due = c;
        }
        Consider(_deltas.DueAt);
        Consider(_queueDebounce.DueAt);
        Consider(_statusDebounce.DueAt);
        Consider(_batchProgressDebounce.DueAt);
        Consider(_throughputTicker.DueAt);
        return due;
    }

    private async Task HandleAsync(Item item, DateTimeOffset now)
    {
        switch (item)
        {
            case LlmItem { Event: ThinkingDelta t }:
                _deltas.Add(true, t.Text, now);
                break;
            case LlmItem { Event: ContentDelta c }:
                _deltas.Add(false, c.Text, now);
                break;
            case LlmItem { Event: var e }:
                // Text before the control event that follows it, whatever the batch clock says.
                await FlushDeltasAsync(now, force: true);
                if (e is RunStarted) _throughputTicker.RunStarted(now);
                if (e is RunEnded) _throughputTicker.RunEnded();
                if (LiveMessageMapper.Control(e) is { } control)
                    await SendAsync(() => _hub.Clients.Group(LiveGroups.StreamLlm).Llm(control));
                break;

            case AudioGenItem { Event: var e }:
                await SendAsync(() => _hub.Clients.Group(LiveGroups.StreamAudio).AudioGen(LiveMessageMapper.Map(e)));
                break;

            case AssemblyItem { Event: AssemblyEncodeProgress p }:
                if (_encodeSteps.Offer(p.Fraction))
                    await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).Assembly(LiveMessageMapper.Map(p)));
                break;
            case AssemblyItem { Event: var e }:
                if (e is AssemblyPhaseStarted) _encodeSteps.Reset();
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).Assembly(LiveMessageMapper.Map(e)));
                break;

            case VoiceBatchItem { Event: BatchProgress p }:
                _pendingBatchProgress = LiveMessageMapper.Map(p);
                _batchProgressDebounce.Mark(now);
                break;
            case VoiceBatchItem { Event: var e }:
                await FlushBatchProgressAsync(now, force: true);
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).VoiceBatch(LiveMessageMapper.Map(e)));
                break;

            case WatchdogItem { Event: var e }:
                var message = LiveMessageMapper.Map(e);
                _watchdogLast[message.Service] = message.Kind;
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).Watchdog(message));
                break;

            case ReceiptItem { Receipt: var r }:
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Project(r.FolderId.Value)).Receipt(ReceiptMessage.From(r)));
                break;

            case SettingsItem { Change: var c }:
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).SettingsChanged(new SettingsChangedMessage(c.Area)));
                break;

            case QueuePulse:
                _queueDebounce.Mark(now);
                break;
            case StatusPulse:
                _statusDebounce.Mark(now);
                break;
        }
    }

    private async Task FlushDueAsync(DateTimeOffset now)
    {
        await FlushDeltasAsync(now, force: false);
        if (_queueDebounce.TryFlush(now))
            await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).Queue(BuildQueueMessage()));
        if (_statusDebounce.TryFlush(now))
            await SendStatusDeltasAsync();
        await FlushBatchProgressAsync(now, force: false);
        if (_throughputTicker.TryTick(now))
            await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).Throughput(_throughput.Snapshot));
    }

    private async Task FlushDeltasAsync(DateTimeOffset now, bool force)
    {
        if (_deltas.Flush(now, force) is var (thinking, content))
            await SendAsync(() => _hub.Clients.Group(LiveGroups.StreamLlm).Llm(LiveMessageMapper.Delta(thinking, content)));
    }

    private async Task FlushBatchProgressAsync(DateTimeOffset now, bool force)
    {
        if (!_batchProgressDebounce.TryFlush(now, force) || _pendingBatchProgress is not { } progress) return;
        _pendingBatchProgress = null;
        await SendAsync(() => _hub.Clients.Group(LiveGroups.Global).VoiceBatch(progress));
    }

    /// <summary>
    /// Recomputes the status maps for every folder somebody has joined, sends only what moved since
    /// the last send, and forgets folders nobody watches any more.
    /// </summary>
    private async Task SendStatusDeltasAsync()
    {
        var active = _registry.ActiveFolders();
        foreach (var stale in _lastSent.Keys.Where(f => !active.Contains(f)).ToList())
            _lastSent.Remove(stale);

        foreach (var folder in active)
        {
            var id = new ProjectFolderId(folder);
            var nodes = _status.Nodes(id);
            var audioRemaining = _status.FolderAudioRemaining(id);
            var paragraphs = _status.Paragraphs(id);
            var items = _status.Items(id);
            var last = _lastSent.GetValueOrDefault(folder)
                       ?? new LastSent(new Dictionary<string, NodeStatusSummary?>(), -1,
                           new Dictionary<string, ParagraphStatusEntry?>(), new Dictionary<string, ItemStatusEntry?>());

            var nodeDelta = ProjectStatusSource.Diff(last.Nodes, nodes);
            if (nodeDelta.Count > 0 || audioRemaining != last.FolderAudioRemaining)
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Project(folder))
                    .NodeStatus(new NodeStatusMessage(folder, nodeDelta, audioRemaining)));

            var paragraphDelta = ProjectStatusSource.Diff(last.Paragraphs, paragraphs);
            var itemDelta = ProjectStatusSource.Diff(last.Items, items);
            if (paragraphDelta.Count > 0 || itemDelta.Count > 0)
                await SendAsync(() => _hub.Clients.Group(LiveGroups.Project(folder))
                    .ItemStatus(new ItemStatusMessage(folder, paragraphDelta, itemDelta)));

            _lastSent[folder] = new LastSent(nodes, audioRemaining, paragraphs, items);
        }
    }

    private async Task SendAsync(Func<Task> send)
    {
        try { await send(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Live hub send failed"); }
    }

    // ---- snapshots (also used by the hub) -----------------------------------------------------------

    public QueueMessage BuildQueueMessage() => new(
        _attributionQueue.Snapshot(),
        _audioQueue.Snapshot(),
        _progress.HasEscalation ? new EscalationState(_progress.Step!.Value, _progress.ConfigName, _progress.ItemCount) : null);

    public LiveSnapshot BuildSnapshot(IEnumerable<string> folders) => new(
        BuildQueueMessage(),
        new AssemblyState(_assemblyService.IsRunning, _assemblyService.CurrentPhase?.ToString(),
            _assemblyService.EncodePercent, _assemblyService.LastError, _assemblyService.AudioRemainingCount),
        new VoiceBatchState(_batchRunner.IsRunning, _batchRunner.Processed, _batchRunner.Total, _batchRunner.Failed,
            _batchRunner.CurrentVoiceName, _batchRunner.CurrentOperation, _batchRunner.LastError),
        new Dictionary<string, string>(_watchdogLast, StringComparer.OrdinalIgnoreCase),
        _throughput.Snapshot,
        folders.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f, f => _status.Snapshot(new ProjectFolderId(f)), StringComparer.OrdinalIgnoreCase));
}
