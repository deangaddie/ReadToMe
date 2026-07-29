using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.NodeStatus;
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
        AttemptState Attempts = default);

    public enum ParagraphQueueStatus { Queued, Processing }

    public enum ParagraphOutcomeKind { Failed, Unknown }

    public sealed record ParagraphOutcome(ParagraphOutcomeKind Kind, string? Reason);

    public sealed record QueueSnapshot(
        int QueuedCount,
        int ProcessingCount,
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

    public sealed class CharacterQueueService : IQueueSource<QueuedParagraph>, IParagraphQueueProbe
    {
        private Channel<QueuedParagraph> _channel =
            Channel.CreateUnbounded<QueuedParagraph>(new UnboundedChannelOptions { SingleReader = true });

        private readonly QueueStateStore<ParagraphKey, ParagraphOutcome> _store = new();

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

            _store.ClearAll();

            Changed?.Invoke();
        }

        public void Enqueue(IEnumerable<QueuedParagraph> paragraphs)
        {
            foreach (var p in paragraphs)
            {
                if (_store.TryMarkQueued(Key(p)))
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
            _store.MarkProcessing(Key(item));
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
            _store.ReturnToQueued(Key(item));
            Changed?.Invoke();
        }

        /// <summary>
        /// Puts an interrupted item back on the queue with a watchdog retry spent (watchdog recovery
        /// path): status returns to Queued and it re-enters the channel, waiting on the closed gate
        /// until recovery reopens it. The once-only budget guards against an endless requeue if the
        /// service is down.
        /// </summary>
        public void Requeue(QueuedParagraph item)
        {
            _store.ReturnToQueued(Key(item));
            _channel.Writer.TryWrite(item with { Attempts = item.Attempts.WithRetry() });
            Changed?.Invoke();
        }

        /// <summary>
        /// Requeues an item whose target model is still loading, re-entering the channel only after
        /// <paramref name="backoff"/> elapses. Distinct from <see cref="Requeue"/>: it spends no
        /// <see cref="AttemptState.Retries"/> (a model load retries indefinitely, never consuming
        /// the watchdog requeue budget) and instead spends an
        /// <see cref="AttemptState.Busies"/> so the next backoff grows. The item's queue
        /// status returns to Queued immediately; the delayed write targets the writer captured here,
        /// so if <see cref="CancelAll"/> swaps the channel while the backoff runs the write lands on
        /// the completed old writer and is dropped — a cancelled item never re-enters the queue.
        /// </summary>
        public void RequeueForModelLoad(QueuedParagraph item, TimeSpan backoff)
        {
            _store.ReturnToQueued(Key(item));

            var next = item with { Attempts = item.Attempts.WithBusy() };
            var writer = _channel.Writer;
            if (backoff <= TimeSpan.Zero)
                writer.TryWrite(next);
            else
                _ = DelayedRequeueAsync(writer, next, backoff, _itemCts.Token);

            Changed?.Invoke();
        }

        /// <summary>
        /// Waits out <paramref name="backoff"/> and writes to the writer captured at schedule time.
        /// Capturing the writer — rather than re-reading the <c>_channel</c> field after the delay —
        /// is what makes the write safe: a <see cref="CancelAll"/> during the delay completes that
        /// writer, so the late write fails harmlessly instead of resurrecting cancelled work on the
        /// replacement channel. <paramref name="ct"/> is not load-bearing for that correctness; it
        /// only ends a pending delay promptly rather than letting it linger.
        /// </summary>
        private static async Task DelayedRequeueAsync(
            ChannelWriter<QueuedParagraph> writer, QueuedParagraph item, TimeSpan backoff, CancellationToken ct)
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
            writer.TryWrite(item);
        }

        /// <summary>
        /// The paragraph is fully attributed: every Character item carries a character. The stamps
        /// themselves reach the UI through <c>ParagraphItemsChanged</c>, published by the apply
        /// command — the queue only carries queue state.
        /// </summary>
        public void MarkComplete(QueuedParagraph item, double? elapsedSeconds)
        {
            var key = Key(item);
            _store.Settle(key, elapsedSeconds: elapsedSeconds);
            Changed?.Invoke();
        }

        public void MarkUnknown(QueuedParagraph item, double? elapsedSeconds, string? reason = null)
        {
            var key = Key(item);
            _store.Settle(key, new ParagraphOutcome(ParagraphOutcomeKind.Unknown, reason), elapsedSeconds);
            Changed?.Invoke();
        }

        public void MarkFailed(QueuedParagraph item, string? reason)
        {
            _store.Abandon(Key(item), new ParagraphOutcome(ParagraphOutcomeKind.Failed, reason));
            Changed?.Invoke();
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => Map(_store.StatusOf(new ParagraphKey(folder, paragraphId)));

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _store.OutcomeOf(new ParagraphKey(folder, paragraphId));

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
        {
            if (_store.ClearOutcome(new ParagraphKey(folder, paragraphId)))
                Changed?.Invoke();
        }

        public bool IsBusy(ProjectFolderId folder, Guid paragraphId) =>
            StatusOf(folder, paragraphId) is not null;

        public QueueSnapshot Snapshot()
        {
            var (queuedCount, processingCount) = _store.CountStatuses();
            var (completed, avg) = _store.Metrics();
            double eta = avg > 0 ? queuedCount * avg : 0;
            var elapsed = _store.CurrentElapsedSeconds();

            return new QueueSnapshot(
                QueuedCount: queuedCount,
                ProcessingCount: processingCount,
                AverageSecondsPerParagraph: avg,
                EstimatedSecondsRemaining: eta,
                CompletedCount: completed,
                CurrentItemElapsedSeconds: elapsed
            );
        }

        private static ParagraphKey Key(QueuedParagraph i) => new(i.Folder, i.ParagraphId);

        /// <summary>
        /// Narrows the shared store's status to the two states callers outside the queue can see.
        /// A settled item (Failed/Unknown) has no status at all — it is read through
        /// <see cref="OutcomeOf"/>.
        /// </summary>
        private static ParagraphQueueStatus? Map(QueueItemStatus? s) => s switch
        {
            QueueItemStatus.Queued => ParagraphQueueStatus.Queued,
            QueueItemStatus.Processing => ParagraphQueueStatus.Processing,
            _ => null,
        };
    }
}
