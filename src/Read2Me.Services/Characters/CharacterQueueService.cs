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

    public sealed class CharacterQueueService : IQueueSource<QueuedParagraph>
    {
        private Channel<QueuedParagraph> _channel =
            Channel.CreateUnbounded<QueuedParagraph>(new UnboundedChannelOptions { SingleReader = true });

        private readonly ParagraphStatusMap _map = new();

        private string? _processingPreview;

        private CancellationTokenSource _itemCts = new();

        public event Action? Changed;

        /// <summary>
        /// Fires when a paragraph is successfully assigned a character by the queue processor.
        /// Subscribers (e.g. BookHierarchyPresenter) should stamp in-memory items and update node status.
        /// </summary>
        public event Action<ProjectFolderId, Guid, ResolvedCharacter>? CharacterAssigned;

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

        public void MarkProcessing(QueuedParagraph item)
        {
            var key = Key(item);
            _map.MarkProcessing(key);
            _processingPreview = item.Preview;
            Changed?.Invoke();
        }

        public void MarkComplete(QueuedParagraph item, double elapsedSeconds, ResolvedCharacter? resolved = null)
        {
            var key = Key(item);
            _map.RemoveOutcome(key);
            if (resolved is not null)
                _map.SetResolved(key, resolved);
            _map.Finish(key, elapsedSeconds);
            _processingPreview = null;
            if (resolved is not null)
                CharacterAssigned?.Invoke(item.Folder, item.ParagraphId, resolved);
            Changed?.Invoke();
        }

        public void MarkUnknown(QueuedParagraph item, double elapsedSeconds)
        {
            var key = Key(item);
            _map.SetOutcome(key, new ParagraphOutcome(ParagraphOutcomeKind.Unknown, null));
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
