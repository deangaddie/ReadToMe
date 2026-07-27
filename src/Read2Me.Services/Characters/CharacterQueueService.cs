using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;


namespace Read2Me.Services.Characters
{
    public readonly record struct QueuedParagraph(
        ProjectFolderId Folder,
        Guid ParagraphId,
        string Preview,
        Guid ChapterId,
        Guid PartId,
        Guid VolumeId,
        bool Requeued = false,
        int LoadAttempts = 0);

    public enum ParagraphQueueStatus { Queued, Processing }

    public enum ParagraphOutcomeKind { Failed, Unknown }

    public sealed record ParagraphOutcome(ParagraphOutcomeKind Kind, string? Reason);

    public sealed record QueueSnapshot(
        int QueuedCount,
        int ProcessingCount,
        string? ProcessingPreview,
        double AverageSecondsPerParagraph,
        double EstimatedSecondsRemaining,
        int CompletedCount,
        double CurrentItemElapsedSeconds
    )
    {
        /// <summary>
        /// Work in hand, globally — one worker drains every folder's queue. Settled outcomes
        /// (Failed/Unknown) are not busy: those paragraphs are finished.
        /// </summary>
        public bool IsBusy => QueuedCount + ProcessingCount > 0;
    }

    internal readonly record struct ParagraphKey(ProjectFolderId Folder, Guid ParagraphId);

    public readonly record struct NodeQueueSummary(bool HasProcessing, int QueuedCount)
    {
        public bool IsEmpty => !HasProcessing && QueuedCount == 0;
    }

    public sealed class CharacterQueueService : IQueueSource<QueuedParagraph>
    {
        private Channel<QueuedParagraph> _channel =
            Channel.CreateUnbounded<QueuedParagraph>(new UnboundedChannelOptions { SingleReader = true });

        private readonly ParagraphStatusMap _map = new();

        private string? _processingPreview;

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
            _processingPreview = null;

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

        /// <summary>
        /// Returns <paramref name="first"/> plus up to <paramref name="max"/>-1 further queued
        /// paragraphs from the same folder and chapter, drained from the head of the queue.
        /// The queue is enqueued in book order, so the result is in book order. Must only be
        /// called from the channel's single reader (the queue worker).
        /// </summary>
        public IReadOnlyList<QueuedParagraph> DrainBatch(QueuedParagraph first, int max)
        {
            var batch = new List<QueuedParagraph> { first };
            while (batch.Count < max &&
                   _channel.Reader.TryPeek(out var next) &&
                   next.Folder == first.Folder &&
                   next.ChapterId == first.ChapterId)
            {
                if (!_channel.Reader.TryRead(out var item))
                    break;
                batch.Add(item);
            }
            return batch;
        }

        /// <summary>
        /// Returns <paramref name="first"/> plus every remaining queued paragraph, across all
        /// chapters and folders, drained from the head of the queue. The queue is enqueued in book
        /// order, so the result is in book order. Same single-reader contract as <see cref="DrainBatch"/>:
        /// only the queue worker calls it, and it marks/resolves/requeues nothing.
        /// </summary>
        public IReadOnlyList<QueuedParagraph> DrainAll(QueuedParagraph first)
        {
            var all = new List<QueuedParagraph> { first };
            while (_channel.Reader.TryRead(out var item))
                all.Add(item);
            return all;
        }

        public void MarkProcessing(QueuedParagraph item)
        {
            var key = Key(item);
            _map.MarkProcessing(key);
            _processingPreview = item.Preview;
            Changed?.Invoke();
        }

        /// <summary>
        /// Returns an in-flight item to Queued *without* putting it back on the channel: the caller
        /// (the attribution chain) still owns it and will re-drive it on a later escalation step.
        /// Used when a chunk's LLM call finishes but leaves an item undecided — it must not sit in
        /// Processing while it waits for the next step's model burst.
        /// </summary>
        public void MarkDeferred(QueuedParagraph item)
        {
            var key = Key(item);
            _map.Requeue(key, item.ChapterId, item.PartId, item.VolumeId);
            _processingPreview = null;
            Changed?.Invoke();
        }

        /// <summary>
        /// Puts an interrupted item back on the queue with its retry flag set (watchdog recovery path):
        /// status returns to Queued and it re-enters the channel, waiting on the closed gate until
        /// recovery reopens it. The flag guards against an endless requeue if the service is down.
        /// </summary>
        public void Requeue(QueuedParagraph item)
        {
            var key = Key(item);
            _map.Requeue(key, item.ChapterId, item.PartId, item.VolumeId);
            _channel.Writer.TryWrite(item with { Requeued = true });
            _processingPreview = null;
            Changed?.Invoke();
        }

        /// <summary>
        /// Requeues an item whose target model is still loading, re-entering the channel only after
        /// <paramref name="backoff"/> elapses. Distinct from <see cref="Requeue"/>: it does NOT set
        /// the <see cref="QueuedParagraph.Requeued"/> once-then-fail flag (a model load retries
        /// indefinitely, never consuming the watchdog requeue budget) and instead bumps
        /// <see cref="QueuedParagraph.LoadAttempts"/> so the next backoff grows. The item's map
        /// status returns to Queued immediately; the delayed write is cancelled if the queue is
        /// cleared while it waits, so a cancelled item never re-enters the channel.
        /// </summary>
        public void RequeueForModelLoad(QueuedParagraph item, TimeSpan backoff)
        {
            var key = Key(item);
            _map.Requeue(key, item.ChapterId, item.PartId, item.VolumeId);
            _processingPreview = null;

            var next = item with { LoadAttempts = item.LoadAttempts + 1 };
            if (backoff <= TimeSpan.Zero)
                _channel.Writer.TryWrite(next);
            else
                _ = DelayedRequeueAsync(next, backoff, _itemCts.Token);

            Changed?.Invoke();
        }

        private async Task DelayedRequeueAsync(QueuedParagraph item, TimeSpan backoff, CancellationToken ct)
        {
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                // The queue was cleared (CancelAll) while this item waited out its backoff — drop it.
                return;
            }
            // Writing to a channel that CancelAll has since swapped is harmless (the old writer is
            // completed), so no extra guard is needed beyond the cancellation above.
            _channel.Writer.TryWrite(item);
        }

        /// <summary>
        /// The paragraph is fully attributed: every Character item carries a character. The stamps
        /// themselves reach the UI through <c>ParagraphItemsChanged</c>, published by the apply
        /// command — the queue only carries queue state.
        /// </summary>
        public void MarkComplete(QueuedParagraph item, double elapsedSeconds)
        {
            var key = Key(item);
            _map.RemoveOutcome(key);
            _map.Finish(key, elapsedSeconds);
            _processingPreview = null;
            Changed?.Invoke();
        }

        public void MarkUnknown(QueuedParagraph item, double elapsedSeconds, string? reason = null)
        {
            var key = Key(item);
            _map.SetOutcome(key, new ParagraphOutcome(ParagraphOutcomeKind.Unknown, reason));
            _map.Finish(key, elapsedSeconds);
            _processingPreview = null;
            Changed?.Invoke();
        }

        public void MarkFailed(QueuedParagraph item, string? reason)
        {
            var key = Key(item);
            _map.SetOutcome(key, new ParagraphOutcome(ParagraphOutcomeKind.Failed, reason));
            _map.DropAncestry(key);
            _processingPreview = null;
            Changed?.Invoke();
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => _map.StatusOf(folder, paragraphId);

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _map.OutcomeOf(folder, paragraphId);

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
            => _map.ClearOutcome(folder, paragraphId);

        public bool IsBusy(ProjectFolderId folder, Guid paragraphId) =>
            StatusOf(folder, paragraphId) is not null;

        public NodeQueueSummary SummaryForNode(ProjectFolderId folder, Guid nodeId)
            => _map.SummaryForNode(folder, nodeId);

        public QueueSnapshot Snapshot()
        {
            var (queuedCount, processingCount) = _map.CountStatuses();
            var (completed, avg) = _map.Metrics();
            double eta = avg > 0 ? queuedCount * avg : 0;
            var elapsed = _map.CurrentElapsedSeconds();

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
    }
}
