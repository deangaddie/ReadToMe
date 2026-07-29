using System.Collections.Concurrent;
using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    public readonly record struct QueuedAudioItem(
        ProjectFolderId Folder, AudioItemRef Item, AttemptState Attempts = default);

    public enum AudioItemQueueStatus { Queued, Processing }

    /// <summary>
    /// Why a settled item is not simply done. <c>Unfinished</c> exists so
    /// <see cref="AudioQueueService.Apply"/> can stay total over <see cref="Disposition"/>: this
    /// queue's phase 1 cannot produce it today (a resolution failure is a <c>Failed</c> work outcome,
    /// not empty work), but throwing would break totality and recording it as <c>Failed</c> would lie
    /// to the UI chip.
    /// </summary>
    public enum AudioItemOutcomeKind { Failed, Unfinished }

    public sealed record AudioItemOutcome(AudioItemOutcomeKind Kind, string? Reason);

    public sealed record AudioQueueSnapshot(
        int QueuedCount,
        int ProcessingCount,
        double AverageSecondsPerItem,
        double EstimatedSecondsRemaining,
        int CompletedCount,
        double CurrentItemElapsedSeconds
    );

    internal readonly record struct AudioItemKey(ProjectFolderId Folder, Guid ParagraphItemId);

    public sealed class AudioQueueService : IQueueSource<QueuedAudioItem>, IAudioQueue
    {
        private Channel<QueuedAudioItem> _channel =
            Channel.CreateUnbounded<QueuedAudioItem>(new UnboundedChannelOptions { SingleReader = true });

        private readonly QueueStateStore<AudioItemKey, AudioItemOutcome> _store = new();
        private readonly ConcurrentDictionary<AudioItemKey, long> _versions = new();

        public event Action? Changed;
        public event Action<ProjectFolderId, Guid, string>? AudioFileAssigned;

        public ChannelReader<QueuedAudioItem> Reader => _channel.Reader;

        public void Enqueue(IEnumerable<QueuedAudioItem> items)
        {
            foreach (var item in items)
            {
                if (_store.TryMarkQueued(Key(item)))
                    _channel.Writer.TryWrite(item);
            }
            Changed?.Invoke();
        }

        public void MarkProcessing(QueuedAudioItem item)
        {
            _store.MarkProcessing(Key(item));
            Changed?.Invoke();
        }

        /// <summary>
        /// Runs the transition the decision produced. One entry point for every outcome, so the
        /// processor names what it decided instead of picking a differently-named method per case.
        /// <para>
        /// Total: every <see cref="Disposition"/> member executes and no arm throws. It performs no
        /// work — resolving, generating and recording stay with the processor, which owns those
        /// collaborators.
        /// </para>
        /// <para>
        /// <see cref="Disposition.Complete"/>'s cache-bust stamp and <see cref="AudioFileAssigned"/>
        /// publish sit <i>inside</i> the arm rather than beside it: they are the transition, and
        /// splitting them out would recreate the two-call sequence <c>Apply</c> exists to remove —
        /// making it possible to complete an item without stamping it. That is why the recorded
        /// relative path rides <see cref="Disposition.Complete.Product"/>.
        /// </para>
        /// <para>
        /// Each retry arm bumps exactly the counter its own <see cref="QueueDisposition.Decide"/> arm
        /// reads: <see cref="Disposition.RetryOnce"/> spends the once-only
        /// <see cref="AttemptState.Retries"/> budget a watchdog recovery is allowed, and
        /// <see cref="Disposition.RetryAfter"/> spends an <see cref="AttemptState.Busies"/> so the
        /// next backoff grows without ever touching the once-only budget.
        /// </para>
        /// </summary>
        public void Apply(QueuedAudioItem item, Disposition disposition)
        {
            var key = Key(item);
            switch (disposition)
            {
                case Disposition.Complete complete:
                    _store.Settle(key, elapsedSeconds: complete.Elapsed);
                    _versions[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    AudioFileAssigned?.Invoke(item.Folder, item.Item.ParagraphItemId, complete.Product!);
                    break;

                case Disposition.Unfinished unfinished:
                    _store.Settle(
                        key,
                        new AudioItemOutcome(AudioItemOutcomeKind.Unfinished, unfinished.Reason),
                        unfinished.Elapsed);
                    break;

                case Disposition.Failed failed:
                    _store.Abandon(key, new AudioItemOutcome(AudioItemOutcomeKind.Failed, failed.Reason));
                    break;

                // Status returns to Queued and the item re-enters the channel, where it waits on the
                // gate closed by recovery until that reopens it.
                case Disposition.RetryOnce:
                    _store.ReturnToQueued(key);
                    _channel.Writer.TryWrite(item with { Attempts = item.Attempts.WithRetry() });
                    break;

                // Status returns to Queued immediately; the write is deferred against the writer
                // captured now, so a CancelAll during the backoff drops the item instead of
                // resurrecting it on the replacement channel. This queue has no item token, so the
                // captured writer is the whole of that guarantee.
                case Disposition.RetryAfter retryAfter:
                    _store.ReturnToQueued(key);
                    _ = DelayedWrite.Schedule(
                        _channel.Writer,
                        item with { Attempts = item.Attempts.WithBusy() },
                        retryAfter.Delay,
                        CancellationToken.None);
                    break;
            }

            Changed?.Invoke();
        }

        public void CancelAll()
        {
            var oldChannel = Interlocked.Exchange(ref _channel,
                Channel.CreateUnbounded<QueuedAudioItem>(new UnboundedChannelOptions { SingleReader = true }));
            oldChannel.Writer.TryComplete();

            _store.ClearAll();
            _versions.Clear();

            Changed?.Invoke();
        }

        public AudioQueueSnapshot Snapshot()
        {
            var (queued, processing) = _store.CountStatuses();
            var (completed, avg) = _store.Metrics();
            double eta = avg > 0 ? queued * avg : 0;
            double elapsed = _store.CurrentElapsedSeconds();
            return new AudioQueueSnapshot(queued, processing, avg, eta, completed, elapsed);
        }

        public AudioItemQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphItemId)
            => Map(_store.StatusOf(new AudioItemKey(folder, paragraphItemId)));

        public AudioItemOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphItemId)
            => _store.OutcomeOf(new AudioItemKey(folder, paragraphItemId));

        public long? AudioVersionOf(ProjectFolderId folder, Guid paragraphItemId)
            => _versions.TryGetValue(new AudioItemKey(folder, paragraphItemId), out var v) ? v : null;

        private static AudioItemKey Key(QueuedAudioItem i) => new(i.Folder, i.Item.ParagraphItemId);

        private static AudioItemQueueStatus? Map(QueueItemStatus? s) => s switch
        {
            QueueItemStatus.Queued => AudioItemQueueStatus.Queued,
            QueueItemStatus.Processing => AudioItemQueueStatus.Processing,
            _ => null,
        };
    }
}
