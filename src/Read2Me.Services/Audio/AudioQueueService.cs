using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    public readonly record struct QueuedAudioItem(ProjectFolderId Folder, AudioItemRef Item);

    public enum AudioItemQueueStatus { Queued, Processing }

    public enum AudioItemOutcomeKind { Failed }

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

    public sealed class AudioQueueService : IQueueSource<QueuedAudioItem>
    {
        private Channel<QueuedAudioItem> _channel =
            Channel.CreateUnbounded<QueuedAudioItem>(new UnboundedChannelOptions { SingleReader = true });

        private readonly QueueStateStore<AudioItemKey, AudioItemOutcome> _store = new();
        private readonly ConcurrentDictionary<AudioItemKey, byte> _complete = new();
        private readonly ConcurrentDictionary<AudioItemKey, long> _versions = new();

        public event Action? Changed;
        public event Action<ProjectFolderId, Guid, string>? AudioFileAssigned;

        public ChannelReader<QueuedAudioItem> Reader => _channel.Reader;

        public void Enqueue(ProjectFolderId folder, IEnumerable<AudioItemRef> items)
        {
            foreach (var item in items)
            {
                var key = new AudioItemKey(folder, item.ParagraphItemId);
                if (_complete.ContainsKey(key)) continue;
                if (_store.TryMarkQueued(key))
                    _channel.Writer.TryWrite(new QueuedAudioItem(folder, item));
            }
            Changed?.Invoke();
        }

        public void MarkProcessing(ProjectFolderId folder, AudioItemRef item)
        {
            _store.MarkProcessing(new AudioItemKey(folder, item.ParagraphItemId));
            Changed?.Invoke();
        }

        public void MarkComplete(ProjectFolderId folder, AudioItemRef item, string relativePath)
        {
            var key = new AudioItemKey(folder, item.ParagraphItemId);
            _store.Finish(key);
            _complete.TryAdd(key, 0);
            _versions[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AudioFileAssigned?.Invoke(folder, item.ParagraphItemId, relativePath);
            Changed?.Invoke();
        }

        public void MarkFailed(ProjectFolderId folder, AudioItemRef item, string? reason)
        {
            var key = new AudioItemKey(folder, item.ParagraphItemId);
            _store.SetOutcome(key, new AudioItemOutcome(AudioItemOutcomeKind.Failed, reason));
            Changed?.Invoke();
        }

        public void CancelAll()
        {
            var oldChannel = Interlocked.Exchange(ref _channel,
                Channel.CreateUnbounded<QueuedAudioItem>(new UnboundedChannelOptions { SingleReader = true }));
            oldChannel.Writer.TryComplete();

            _store.ClearAll();
            _complete.Clear();
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

        private static AudioItemQueueStatus? Map(QueueItemStatus? s) => s switch
        {
            QueueItemStatus.Queued => AudioItemQueueStatus.Queued,
            QueueItemStatus.Processing => AudioItemQueueStatus.Processing,
            _ => null,
        };
    }
}
