using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.History;
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
        AttemptState Attempts = default) : IQueuedWork<QueuedParagraph>
    {
        public WorkItemKey Key => new(Folder, ParagraphId);

        public QueuedParagraph WithAttempts(AttemptState attempts) => this with { Attempts = attempts };
    }

    public enum ParagraphQueueStatus { Queued, Processing }

    /// <summary>The attribution queue's counts and timings, answered by <c>GET /api/attribution/queue</c>.</summary>
    public sealed record AttributionQueueSnapshot(
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

    /// <summary>
    /// The <b>Character Queue</b>: a <see cref="WorkQueue{TItem}"/> of paragraphs. Everything a Work
    /// queue does is the module's; this adds the paragraph-shaped reads its callers ask.
    /// </summary>
    public sealed class CharacterQueueService : IQueueSource<QueuedParagraph>, IParagraphQueueProbe, ICharacterQueue
    {
        private readonly WorkQueue<QueuedParagraph> _queue;

        public CharacterQueueService(ProjectActivity activity)
        {
            _queue = new WorkQueue<QueuedParagraph>(
                activity, new WorkQueueBusy(BusySourceKind.CharacterQueue, "Attribution", "paragraph"));
        }

        public event Action? Changed
        {
            add => _queue.Changed += value;
            remove => _queue.Changed -= value;
        }

        /// <inheritdoc cref="WorkQueue{TItem}.Cancelling"/>
        public event Action? Cancelling
        {
            add => _queue.Cancelling += value;
            remove => _queue.Cancelling -= value;
        }

        public ChannelReader<QueuedParagraph> Reader => _queue.Reader;

        public CancellationToken ItemCancellationToken => _queue.ItemCancellationToken;

        public void CancelAll() => _queue.CancelAll();

        public ActivityRefusal? Enqueue(IEnumerable<QueuedParagraph> paragraphs) => _queue.Enqueue(paragraphs);

        /// <summary>
        /// Returns <paramref name="first"/> plus every remaining queued paragraph, across all
        /// chapters and folders. The queue is enqueued in book order, so the result is in book order.
        /// Only the queue worker calls it, and it marks/resolves/requeues nothing.
        /// </summary>
        public IReadOnlyList<QueuedParagraph> DrainAll(QueuedParagraph first) => _queue.DrainAll(first);

        public void MarkProcessing(QueuedParagraph item) => _queue.MarkProcessing(item);

        /// <summary>
        /// Returns an in-flight item to Queued *without* putting it back on the channel: the caller
        /// (the attribution chain) still owns it and will re-drive it on a later escalation step.
        /// Used when a chunk's LLM call finishes but leaves an item undecided — it must not sit in
        /// Processing while it waits for the next step's model burst.
        /// </summary>
        public void MarkDeferred(QueuedParagraph item) => _queue.Defer(item);

        /// <inheritdoc cref="WorkQueue{TItem}.Apply"/>
        /// <remarks>The stamps themselves reach every open Book View through the receipt the apply commits (ADR 0007).</remarks>
        public void Apply(QueuedParagraph item, Disposition disposition) => _queue.Apply(item, disposition);

        /// <summary>
        /// Paragraphs of <paramref name="folder"/> the queue currently says anything about: queued,
        /// processing, or carrying a Queue outcome. The live hub relay diffs per-item status from
        /// this set; single-row reads use <see cref="StatusOf"/>.
        /// </summary>
        public IReadOnlyCollection<Guid> KnownParagraphs(ProjectFolderId folder)
            => _queue.KnownKeys().Where(k => k.Folder == folder).Select(k => k.Id).Distinct().ToArray();

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => Map(_queue.StatusOf(new WorkItemKey(folder, paragraphId)));

        public QueueOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _queue.OutcomeOf(new WorkItemKey(folder, paragraphId));

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
            => _queue.ClearOutcome(new WorkItemKey(folder, paragraphId));

        public bool IsBusy(ProjectFolderId folder, Guid paragraphId) =>
            StatusOf(folder, paragraphId) is not null;

        /// <summary>The paragraphs still queued or processing, by folder; a folder with none is absent.</summary>
        public ILookup<ProjectFolderId, Guid> PendingParagraphs() =>
            _queue.TrackedKeys().ToLookup(k => k.Folder, k => k.Id);

        /// <inheritdoc cref="WorkQueue{TItem}.BusyFor"/>
        public BusySource? BusyFor(ProjectFolderId folder) => _queue.BusyFor(folder);

        public AttributionQueueSnapshot Snapshot()
        {
            var c = _queue.Counts();
            return new AttributionQueueSnapshot(
                QueuedCount: c.Queued,
                ProcessingCount: c.Processing,
                AverageSecondsPerParagraph: c.AverageSeconds,
                EstimatedSecondsRemaining: c.EstimatedSecondsRemaining,
                CompletedCount: c.Completed,
                CurrentItemElapsedSeconds: c.CurrentItemElapsedSeconds
            );
        }

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
