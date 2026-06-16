using System;
using System.Collections.Generic;
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

        private readonly ParagraphStatusMap _map = new();
        private readonly QueueMetrics _metrics = new();

        private ParagraphKey? _processingKey;
        private string? _processingPreview;
        private DateTimeOffset? _processingStartedAt;

        private CancellationTokenSource _itemCts = new();

        public event Action? Changed;

        public CharacterQueueService()
        {
            _map.Changed += () => Changed?.Invoke();
        }

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

            _map.ClearAll();
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
                if (_map.TryMarkQueued(key, p.ChapterId, p.PartId, p.VolumeId))
                    _channel.Writer.TryWrite(p);
            }
            Changed?.Invoke();
        }

        public void MarkProcessing(QueuedParagraph item)
        {
            var key = Key(item);
            _map.MarkProcessing(key);
            _processingKey = key;
            _processingPreview = item.Preview;
            _processingStartedAt = DateTimeOffset.UtcNow;
            Changed?.Invoke();
        }

        public void MarkComplete(QueuedParagraph item, double elapsedSeconds, ResolvedCharacter? resolved = null)
        {
            var key = Key(item);
            _map.RemoveOutcome(key);
            if (resolved is not null)
                _map.SetResolved(key, resolved);
            Finish(key, elapsedSeconds);
        }

        public void MarkUnknown(QueuedParagraph item, double elapsedSeconds)
        {
            var key = Key(item);
            _map.SetOutcome(key, new ParagraphOutcome(ParagraphOutcomeKind.Unknown, null));
            Finish(key, elapsedSeconds);
        }

        public void MarkFailed(QueuedParagraph item, string? reason)
        {
            var key = Key(item);
            _map.SetOutcome(key, new ParagraphOutcome(ParagraphOutcomeKind.Failed, reason));
            Finish(key, null);
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => _map.StatusOf(folder, paragraphId);

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _map.OutcomeOf(folder, paragraphId);

        public ResolvedCharacter? ResolvedOf(ProjectFolderId folder, Guid paragraphId)
            => _map.ResolvedOf(folder, paragraphId);

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
            => _map.ClearOutcome(folder, paragraphId);

        public bool IsBusy(ProjectFolderId folder, Guid paragraphId) =>
            StatusOf(folder, paragraphId) is not null;

        public NodeQueueSummary SummaryForNode(ProjectFolderId folder, Guid nodeId)
            => _map.SummaryForNode(folder, nodeId);

        public QueueSnapshot Snapshot()
        {
            var (queuedCount, processingCount) = _map.CountStatuses();
            var (completed, avg) = _metrics.Read();
            double eta = avg > 0 ? queuedCount * avg : 0;
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
            _map.RemoveEntry(key);
            _processingKey = null;
            _processingPreview = null;
            _processingStartedAt = null;

            if (elapsedSeconds is double elapsed)
                _metrics.RecordCompletion(elapsed);

            Changed?.Invoke();
        }
    }
}
