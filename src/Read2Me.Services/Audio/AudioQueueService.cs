using System.Collections.Concurrent;
using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    public readonly record struct QueuedAudioItem(
        ProjectFolderId Folder, AudioItemRef Item, AttemptState Attempts = default) : IQueuedWork<QueuedAudioItem>
    {
        public WorkItemKey Key => new(Folder, Item.ParagraphItemId);

        public QueuedAudioItem WithAttempts(AttemptState attempts) => this with { Attempts = attempts };
    }

    public enum AudioItemQueueStatus { Queued, Processing }

    /// <summary>The audio queue's counts and timings, answered by <c>GET /api/audio/queue</c>.</summary>
    public sealed record AudioQueueSnapshot(
        int QueuedCount,
        int ProcessingCount,
        double AverageSecondsPerItem,
        double EstimatedSecondsRemaining,
        int CompletedCount,
        double CurrentItemElapsedSeconds
    );

    /// <summary>
    /// The <b>Audio Queue</b>: a <see cref="WorkQueue{TItem}"/> of ParagraphItems. What it adds is the
    /// <b>audio version</b> — a cache-bust stamp each completion takes, inside the Complete arm, so an
    /// item cannot complete without one. The recorded audio itself is not this queue's business: the
    /// item's audio reference and its review are one Book mutation the recorder commits, and every
    /// open Book View converges on its receipt (ADR 0007).
    /// </summary>
    public sealed class AudioQueueService : IQueueSource<QueuedAudioItem>, IAudioQueue
    {
        private readonly WorkQueue<QueuedAudioItem> _queue;
        private readonly ConcurrentDictionary<WorkItemKey, long> _versions = new();

        public AudioQueueService(ProjectActivity activity)
        {
            _queue = new WorkQueue<QueuedAudioItem>(
                activity,
                new WorkQueueBusy(BusySourceKind.AudioQueue, "Audio", "item"),
                onComplete: key => _versions[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                onCancel: _versions.Clear);
        }

        public event Action? Changed
        {
            add => _queue.Changed += value;
            remove => _queue.Changed -= value;
        }

        public ChannelReader<QueuedAudioItem> Reader => _queue.Reader;

        public CancellationToken ItemCancellationToken => _queue.ItemCancellationToken;

        public ActivityRefusal? Enqueue(IEnumerable<QueuedAudioItem> items) => _queue.Enqueue(items);

        public void MarkProcessing(QueuedAudioItem item) => _queue.MarkProcessing(item);

        /// <inheritdoc cref="WorkQueue{TItem}.Apply"/>
        public void Apply(QueuedAudioItem item, Disposition disposition) => _queue.Apply(item, disposition);

        /// <inheritdoc cref="WorkQueue{TItem}.CancelAll"/>
        public void CancelAll() => _queue.CancelAll();

        public AudioQueueSnapshot Snapshot()
        {
            var c = _queue.Counts();
            return new AudioQueueSnapshot(
                c.Queued, c.Processing, c.AverageSeconds, c.EstimatedSecondsRemaining, c.Completed, c.CurrentItemElapsedSeconds);
        }

        /// <summary>
        /// Items of <paramref name="folder"/> the queue currently says anything about: queued,
        /// processing, carrying a Queue outcome, or with a bumped audio version. Feeds the live hub relay.
        /// </summary>
        public IReadOnlyCollection<Guid> KnownItems(ProjectFolderId folder)
            => _queue.KnownKeys().Concat(_versions.Keys)
                .Where(k => k.Folder == folder).Select(k => k.Id).Distinct().ToArray();

        public AudioItemQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphItemId)
            => Map(_queue.StatusOf(new WorkItemKey(folder, paragraphItemId)));

        public QueueOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphItemId)
            => _queue.OutcomeOf(new WorkItemKey(folder, paragraphItemId));

        public long? AudioVersionOf(ProjectFolderId folder, Guid paragraphItemId)
            => _versions.TryGetValue(new WorkItemKey(folder, paragraphItemId), out var v) ? v : null;

        /// <inheritdoc cref="WorkQueue{TItem}.BusyFor"/>
        public BusySource? BusyFor(ProjectFolderId folder) => _queue.BusyFor(folder);

        private static AudioItemQueueStatus? Map(QueueItemStatus? s) => s switch
        {
            QueueItemStatus.Queued => AudioItemQueueStatus.Queued,
            QueueItemStatus.Processing => AudioItemQueueStatus.Processing,
            _ => null,
        };
    }
}
