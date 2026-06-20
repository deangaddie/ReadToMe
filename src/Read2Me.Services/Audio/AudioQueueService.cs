using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;

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

    public sealed class AudioQueueService
    {
        private Channel<QueuedAudioItem> _channel =
            Channel.CreateUnbounded<QueuedAudioItem>(new UnboundedChannelOptions { SingleReader = true });

        private readonly ConcurrentDictionary<AudioItemKey, AudioItemQueueStatus> _status = new();
        private readonly ConcurrentDictionary<AudioItemKey, AudioItemOutcome> _outcomes = new();
        private readonly ConcurrentHashSet _complete = new();
        private readonly ConcurrentDictionary<AudioItemKey, long> _versions = new();
        private readonly QueueMetrics _metrics = new();
        private DateTimeOffset? _processingStartedAt;

        public event Action? Changed;
        public event Action<ProjectFolderId, Guid, string>? AudioFileAssigned;

        public ChannelReader<QueuedAudioItem> Reader => _channel.Reader;

        public void Enqueue(ProjectFolderId folder, IEnumerable<AudioItemRef> items)
        {
            foreach (var item in items)
            {
                var key = new AudioItemKey(folder, item.ParagraphItemId);
                _complete.Remove(key);
                _outcomes.TryRemove(key, out _);
                if (_status.TryAdd(key, AudioItemQueueStatus.Queued))
                    _channel.Writer.TryWrite(new QueuedAudioItem(folder, item));
            }
            Changed?.Invoke();
        }

        public void MarkProcessing(ProjectFolderId folder, AudioItemRef item)
        {
            var key = new AudioItemKey(folder, item.ParagraphItemId);
            _status[key] = AudioItemQueueStatus.Processing;
            _processingStartedAt = DateTimeOffset.UtcNow;
            Changed?.Invoke();
        }

        public void MarkComplete(ProjectFolderId folder, AudioItemRef item, string relativePath)
        {
            var key = new AudioItemKey(folder, item.ParagraphItemId);
            _status.TryRemove(key, out _);
            _outcomes.TryRemove(key, out _);
            _complete.Add(key);
            _versions[key] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_processingStartedAt.HasValue)
                _metrics.RecordCompletion((DateTimeOffset.UtcNow - _processingStartedAt.Value).TotalSeconds);
            _processingStartedAt = null;
            AudioFileAssigned?.Invoke(folder, item.ParagraphItemId, relativePath);
            Changed?.Invoke();
        }

        public void MarkFailed(ProjectFolderId folder, AudioItemRef item, string? reason)
        {
            var key = new AudioItemKey(folder, item.ParagraphItemId);
            _outcomes[key] = new AudioItemOutcome(AudioItemOutcomeKind.Failed, reason);
            _status.TryRemove(key, out _);
            _processingStartedAt = null;
            Changed?.Invoke();
        }

        public void CancelAll()
        {
            var oldChannel = Interlocked.Exchange(ref _channel,
                Channel.CreateUnbounded<QueuedAudioItem>(new UnboundedChannelOptions { SingleReader = true }));
            oldChannel.Writer.TryComplete();

            _status.Clear();
            _outcomes.Clear();
            _complete.Clear();
            _versions.Clear();
            _processingStartedAt = null;

            Changed?.Invoke();
        }

        public AudioQueueSnapshot Snapshot()
        {
            int queued = 0, processing = 0;
            foreach (var s in _status.Values)
            {
                if (s == AudioItemQueueStatus.Queued) queued++;
                else processing++;
            }
            var (completed, avg) = _metrics.Read();
            double eta = avg > 0 ? queued * avg : 0;
            double elapsed = _processingStartedAt.HasValue
                ? (DateTimeOffset.UtcNow - _processingStartedAt.Value).TotalSeconds
                : 0;
            return new AudioQueueSnapshot(queued, processing, avg, eta, completed, elapsed);
        }

        public AudioItemQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphItemId)
        {
            var key = new AudioItemKey(folder, paragraphItemId);
            return _status.TryGetValue(key, out var s) ? s : null;
        }

        public AudioItemOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphItemId)
        {
            var key = new AudioItemKey(folder, paragraphItemId);
            return _outcomes.TryGetValue(key, out var o) ? o : null;
        }

        public long? AudioVersionOf(ProjectFolderId folder, Guid paragraphItemId)
        {
            var key = new AudioItemKey(folder, paragraphItemId);
            return _versions.TryGetValue(key, out var v) ? v : null;
        }
    }

    internal sealed class ConcurrentHashSet
    {
        private readonly ConcurrentDictionary<AudioItemKey, byte> _inner = new();

        public bool Contains(AudioItemKey key) => _inner.ContainsKey(key);
        public void Add(AudioItemKey key) => _inner.TryAdd(key, 0);
        public void Remove(AudioItemKey key) => _inner.TryRemove(key, out _);
        public void Clear() => _inner.Clear();
    }
}
