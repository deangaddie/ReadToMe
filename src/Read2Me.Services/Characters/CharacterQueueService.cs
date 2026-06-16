using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using Read2Me.Core.Models;

namespace Read2Me.Services.Characters
{
    public readonly record struct QueuedParagraph(
        ProjectFolderId Folder,
        Guid ParagraphId,
        string Preview,
        Guid ChapterId,
        Guid PartId,
        Guid VolumeId);

    public enum ParagraphQueueStatus { Queued, Processing }

    public enum ParagraphOutcomeKind { Failed, Unknown }

    public sealed record ParagraphOutcome(ParagraphOutcomeKind Kind, string? Reason);

    public sealed record ResolvedCharacter(Guid CharacterId, string Name);

    public sealed record QueueSnapshot(
        int QueuedCount,
        int ProcessingCount,
        string? ProcessingPreview,
        double AverageSecondsPerParagraph,
        double EstimatedSecondsRemaining,
        int CompletedCount,
        double CurrentItemElapsedSeconds
    );

    internal readonly record struct ParagraphKey(ProjectFolderId Folder, Guid ParagraphId);

    public readonly record struct NodeQueueSummary(bool HasProcessing, int QueuedCount)
    {
        public bool IsEmpty => !HasProcessing && QueuedCount == 0;
    }

    public sealed class CharacterQueueService
    {
        private Channel<QueuedParagraph> _channel =
            Channel.CreateUnbounded<QueuedParagraph>(new UnboundedChannelOptions { SingleReader = true });

        private readonly ConcurrentDictionary<ParagraphKey, ParagraphQueueStatus> _status = new();
        private readonly ConcurrentDictionary<ParagraphKey, (Guid Chapter, Guid Part, Guid Volume)> _ancestry = new();
        private readonly ConcurrentDictionary<ParagraphKey, ParagraphOutcome> _outcomes = new();
        private readonly ConcurrentDictionary<ParagraphKey, ResolvedCharacter> _resolved = new();

        private ParagraphKey? _processingKey;
        private string? _processingPreview;
        private DateTimeOffset? _processingStartedAt;
        private int _completedCount;
        private double _averageSecondsPerParagraph;
        private readonly Lock _metricsLock = new();

        private CancellationTokenSource _itemCts = new();

        public event Action? Changed;

        public ChannelReader<QueuedParagraph> Reader => _channel.Reader;

        public CancellationToken ItemCancellationToken => _itemCts.Token;

        public void CancelAll()
        {
            var old = Interlocked.Exchange(ref _itemCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();

            var oldChannel = Interlocked.Exchange(ref _channel,
                Channel.CreateUnbounded<QueuedParagraph>(new UnboundedChannelOptions { SingleReader = true }));
            oldChannel.Writer.TryComplete();

            _status.Clear();
            _ancestry.Clear();
            _resolved.Clear();
            _processingKey = null;
            _processingPreview = null;
            _processingStartedAt = null;

            Changed?.Invoke();
        }

        public void Enqueue(IEnumerable<QueuedParagraph> paragraphs)
        {
            foreach (var p in paragraphs)
            {
                var key = Key(p);
                _outcomes.TryRemove(key, out _);
                _resolved.TryRemove(key, out _);
                if (_status.TryAdd(key, ParagraphQueueStatus.Queued))
                {
                    _ancestry[key] = (p.ChapterId, p.PartId, p.VolumeId);
                    _channel.Writer.TryWrite(p);
                }
            }
            Changed?.Invoke();
        }

        public void MarkProcessing(QueuedParagraph item)
        {
            var key = Key(item);
            _status[key] = ParagraphQueueStatus.Processing;
            _processingKey = key;
            _processingPreview = item.Preview;
            _processingStartedAt = DateTimeOffset.UtcNow;
            Changed?.Invoke();
        }

        public void MarkComplete(QueuedParagraph item, double elapsedSeconds, ResolvedCharacter? resolved = null)
        {
            var key = Key(item);
            _outcomes.TryRemove(key, out _);
            if (resolved is not null)
                _resolved[key] = resolved;
            Finish(key, elapsedSeconds);
        }

        public ResolvedCharacter? ResolvedOf(ProjectFolderId folder, Guid paragraphId)
            => _resolved.TryGetValue(new ParagraphKey(folder, paragraphId), out var r) ? r : null;

        public void MarkUnknown(QueuedParagraph item, double elapsedSeconds)
        {
            var key = Key(item);
            _outcomes[key] = new ParagraphOutcome(ParagraphOutcomeKind.Unknown, null);
            Finish(key, elapsedSeconds);
        }

        public void MarkFailed(QueuedParagraph item, string? reason)
        {
            var key = Key(item);
            _outcomes[key] = new ParagraphOutcome(ParagraphOutcomeKind.Failed, reason);
            Finish(key, null);
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            return _status.TryGetValue(key, out var s) ? s : null;
        }

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _outcomes.TryGetValue(new ParagraphKey(folder, paragraphId), out var o) ? o : null;

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            var removed = _outcomes.TryRemove(key, out _);
            removed |= _resolved.TryRemove(key, out _);
            if (removed)
                Changed?.Invoke();
        }

        public bool IsBusy(ProjectFolderId folder, Guid paragraphId) =>
            StatusOf(folder, paragraphId) is not null;

        public NodeQueueSummary SummaryForNode(ProjectFolderId folder, Guid nodeId)
        {
            bool hasProcessing = false;
            int queued = 0;
            foreach (var (key, status) in _status)
            {
                if (key.Folder != folder) continue;
                if (!_ancestry.TryGetValue(key, out var anc)) continue;
                if (anc.Chapter != nodeId && anc.Part != nodeId && anc.Volume != nodeId) continue;
                if (status == ParagraphQueueStatus.Processing) hasProcessing = true;
                else queued++;
            }
            return new NodeQueueSummary(hasProcessing, queued);
        }

        public QueueSnapshot Snapshot()
        {
            int queuedCount = 0;
            int processingCount = 0;
            foreach (var s in _status.Values)
            {
                if (s == ParagraphQueueStatus.Queued) queuedCount++;
                else processingCount++;
            }

            double avg, eta;
            int completed;
            lock (_metricsLock)
            {
                avg = _averageSecondsPerParagraph;
                completed = _completedCount;
            }
            eta = avg > 0 ? queuedCount * avg : 0;

            var elapsed = _processingStartedAt.HasValue
                ? (DateTimeOffset.UtcNow - _processingStartedAt.Value).TotalSeconds
                : 0;

            return new QueueSnapshot(
                QueuedCount: queuedCount,
                ProcessingCount: processingCount,
                ProcessingPreview: _processingPreview,
                AverageSecondsPerParagraph: avg,
                EstimatedSecondsRemaining: eta,
                CompletedCount: completed,
                CurrentItemElapsedSeconds: elapsed
            );
        }

        private static ParagraphKey Key(QueuedParagraph i) => new(i.Folder, i.ParagraphId);

        private void Finish(ParagraphKey key, double? elapsedSeconds)
        {
            _status.TryRemove(key, out _);
            _ancestry.TryRemove(key, out _);
            _processingKey = null;
            _processingPreview = null;
            _processingStartedAt = null;

            if (elapsedSeconds is double elapsed)
            {
                lock (_metricsLock)
                {
                    _completedCount++;
                    _averageSecondsPerParagraph = _completedCount == 1
                        ? elapsed
                        : (_averageSecondsPerParagraph * (_completedCount - 1) + elapsed) / _completedCount;
                }
            }

            Changed?.Invoke();
        }
    }
}
