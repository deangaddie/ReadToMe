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

    public enum ParagraphOutcomeKind { Failed, Unfinished }

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
        /// (Failed/Unfinished) are not busy: those paragraphs are finished.
        /// </summary>
        public bool IsBusy => QueuedCount + ProcessingCount > 0;
    }

    internal readonly record struct ParagraphKey(ProjectFolderId Folder, Guid ParagraphId);

    public sealed class CharacterQueueService : IQueueSource<QueuedParagraph>, IParagraphQueueProbe, ICharacterQueue
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
        /// Runs the transition the decision produced. One entry point for every outcome, so the
        /// processor names what it decided instead of picking a differently-named method per case.
        /// <para>
        /// Total: every <see cref="Disposition"/> member executes and no arm throws. It performs no
        /// work — applying the answer and probing what it left behind stay with the processor, which
        /// owns those collaborators.
        /// </para>
        /// <para>
        /// Each retry arm bumps exactly the counter its own <see cref="QueueDisposition.Decide"/> arm
        /// reads: <see cref="Disposition.RetryOnce"/> spends the once-only
        /// <see cref="AttemptState.Retries"/> budget that a watchdog recovery is allowed, and
        /// <see cref="Disposition.RetryAfter"/> spends an <see cref="AttemptState.Busies"/> so the
        /// next backoff grows without ever touching the once-only budget. That makes the two budgets
        /// independent structurally rather than by comment.
        /// </para>
        /// </summary>
        public void Apply(QueuedParagraph item, Disposition disposition)
        {
            var key = Key(item);
            switch (disposition)
            {
                // The stamps themselves reach the UI through ParagraphItemsChanged, published by the
                // apply command — the queue only carries queue state.
                case Disposition.Complete complete:
                    _store.Settle(key, elapsedSeconds: complete.Elapsed);
                    break;

                case Disposition.Unfinished unfinished:
                    _store.Settle(
                        key,
                        new ParagraphOutcome(ParagraphOutcomeKind.Unfinished, unfinished.Reason),
                        unfinished.Elapsed);
                    break;

                case Disposition.Failed failed:
                    _store.Abandon(key, new ParagraphOutcome(ParagraphOutcomeKind.Failed, failed.Reason));
                    break;

                // Status returns to Queued and the item re-enters the channel, where it waits on the
                // gate closed by recovery until that reopens it.
                case Disposition.RetryOnce:
                    _store.ReturnToQueued(key);
                    _channel.Writer.TryWrite(item with { Attempts = item.Attempts.WithRetry() });
                    break;

                // Status returns to Queued immediately; the write is deferred against the writer
                // captured now, so a CancelAll during the backoff drops the item instead of
                // resurrecting it on the replacement channel.
                case Disposition.RetryAfter retryAfter:
                    _store.ReturnToQueued(key);
                    _ = DelayedWrite.Schedule(
                        _channel.Writer,
                        item with { Attempts = item.Attempts.WithBusy() },
                        retryAfter.Delay,
                        _itemCts.Token);
                    break;
            }

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
        /// A settled item (Failed/Unfinished) has no status at all — it is read through
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
