using System.Collections.Concurrent;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Character-side view over the shared <see cref="QueueStateStore{TKey,TOutcome}"/>,
    /// adding the two concerns that do not generalize: node ancestry (for
    /// <see cref="SummaryForNode"/> roll-up) and the resolved-character overlay.
    /// </summary>
    internal sealed class ParagraphStatusMap
    {
        private readonly QueueStateStore<ParagraphKey, ParagraphOutcome> _store = new();
        private readonly ConcurrentDictionary<ParagraphKey, (Guid Chapter, Guid Part, Guid Volume)> _ancestry = new();
        private readonly ConcurrentDictionary<ParagraphKey, ResolvedCharacter> _resolved = new();

        public event Action? Changed;

        public bool TryMarkQueued(ParagraphKey key, Guid chapter, Guid part, Guid volume)
        {
            _resolved.TryRemove(key, out _);
            if (_store.TryMarkQueued(key))
            {
                _ancestry[key] = (chapter, part, volume);
                return true;
            }
            return false;
        }

        public void MarkProcessing(ParagraphKey key) => _store.MarkProcessing(key);

        public void RemoveOutcome(ParagraphKey key) => _store.RemoveOutcome(key);

        public void SetResolved(ParagraphKey key, ResolvedCharacter resolved) => _resolved[key] = resolved;

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
            var removed = _store.ClearOutcome(key);
            removed |= _resolved.TryRemove(key, out _);
            if (removed) Changed?.Invoke();
        }

        public void ClearAll()
        {
            _store.ClearAll();
            _ancestry.Clear();
            _resolved.Clear();
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => Map(_store.StatusOf(new ParagraphKey(folder, paragraphId)));

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _store.OutcomeOf(new ParagraphKey(folder, paragraphId));

        public ResolvedCharacter? ResolvedOf(ProjectFolderId folder, Guid paragraphId)
            => _resolved.TryGetValue(new ParagraphKey(folder, paragraphId), out var r) ? r : null;

        public NodeQueueSummary SummaryForNode(ProjectFolderId folder, Guid nodeId)
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
            return new NodeQueueSummary(hasProcessing, queued);
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
