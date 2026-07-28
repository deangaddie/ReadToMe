using System.Collections.Concurrent;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Character-side view over the shared <see cref="QueueStateStore{TKey,TOutcome}"/>, adding the
    /// one concern that does not generalize: node ancestry (for <see cref="SummaryForNode"/> roll-up).
    /// </summary>
    internal sealed class ParagraphStatusMap
    {
        private readonly QueueStateStore<ParagraphKey, ParagraphOutcome> _store = new();
        private readonly ConcurrentDictionary<ParagraphKey, (Guid Chapter, Guid Part, Guid Volume)> _ancestry = new();

        public event Action? Changed;

        public bool TryMarkQueued(ParagraphKey key, Guid chapter, Guid part, Guid volume)
        {
            if (_store.TryMarkQueued(key))
            {
                _ancestry[key] = (chapter, part, volume);
                return true;
            }
            return false;
        }

        public void MarkProcessing(ParagraphKey key) => _store.MarkProcessing(key);

        /// <summary>Returns the key to Queued (retaining ancestry) so an interrupted item can be re-driven.</summary>
        public void Requeue(ParagraphKey key, Guid chapter, Guid part, Guid volume)
        {
            _ancestry[key] = (chapter, part, volume);
            _store.Requeue(key);
        }

        public void RemoveOutcome(ParagraphKey key) => _store.RemoveOutcome(key);

        public void SetOutcome(ParagraphKey key, ParagraphOutcome outcome) => _store.SetOutcome(key, outcome);

        /// <summary>Removes the entry on successful completion and records elapsed time.</summary>
        public void Finish(ParagraphKey key, double elapsedSeconds)
        {
            _ancestry.TryRemove(key, out _);
            _store.Finish(key, elapsedSeconds);
        }

        /// <summary>Drops the entry from ancestry without recording a completion (failed path).</summary>
        public void DropAncestry(ParagraphKey key) => _ancestry.TryRemove(key, out _);

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_store.ClearOutcome(key)) Changed?.Invoke();
        }

        /// <summary>Drops active (queued/processing) tracking and its ancestry roll-up.</summary>
        public void ClearAll()
        {
            _store.ClearAll();
            _ancestry.Clear();
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => Map(_store.StatusOf(new ParagraphKey(folder, paragraphId)));

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _store.OutcomeOf(new ParagraphKey(folder, paragraphId));

        public (bool HasProcessing, int QueuedCount) SummaryForNode(ProjectFolderId folder, Guid nodeId)
        {
            bool hasProcessing = false;
            int queued = 0;
            foreach (var (key, anc) in _ancestry)
            {
                if (key.Folder != folder) continue;
                if (anc.Chapter != nodeId && anc.Part != nodeId && anc.Volume != nodeId) continue;
                var status = _store.StatusOf(key);
                if (status == QueueItemStatus.Processing) hasProcessing = true;
                else if (status == QueueItemStatus.Queued) queued++;
            }
            return (hasProcessing, queued);
        }

        public (int queued, int processing) CountStatuses() => _store.CountStatuses();

        public (int completed, double avg) Metrics() => _store.Metrics();

        public double CurrentElapsedSeconds() => _store.CurrentElapsedSeconds();

        private static ParagraphQueueStatus? Map(QueueItemStatus? s) => s switch
        {
            QueueItemStatus.Queued => ParagraphQueueStatus.Queued,
            QueueItemStatus.Processing => ParagraphQueueStatus.Processing,
            _ => null,
        };
    }
}
