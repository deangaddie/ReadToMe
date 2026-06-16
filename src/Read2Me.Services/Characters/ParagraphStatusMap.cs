using System;
using System.Collections.Concurrent;
using Read2Me.Core.Models;

namespace Read2Me.Services.Characters
{
    internal sealed class ParagraphStatusMap
    {
        private readonly ConcurrentDictionary<ParagraphKey, ParagraphQueueStatus> _status = new();
        private readonly ConcurrentDictionary<ParagraphKey, (Guid Chapter, Guid Part, Guid Volume)> _ancestry = new();
        private readonly ConcurrentDictionary<ParagraphKey, ParagraphOutcome> _outcomes = new();
        private readonly ConcurrentDictionary<ParagraphKey, ResolvedCharacter> _resolved = new();

        public event Action? Changed;

        public bool TryMarkQueued(ParagraphKey key, Guid chapter, Guid part, Guid volume)
        {
            _outcomes.TryRemove(key, out _);
            _resolved.TryRemove(key, out _);
            if (_status.TryAdd(key, ParagraphQueueStatus.Queued))
            {
                _ancestry[key] = (chapter, part, volume);
                return true;
            }
            return false;
        }

        public void MarkProcessing(ParagraphKey key)
        {
            _status[key] = ParagraphQueueStatus.Processing;
        }

        public void RemoveOutcome(ParagraphKey key)
        {
            _outcomes.TryRemove(key, out _);
        }

        public void SetResolved(ParagraphKey key, ResolvedCharacter resolved)
        {
            _resolved[key] = resolved;
        }

        public void SetOutcome(ParagraphKey key, ParagraphOutcome outcome)
        {
            _outcomes[key] = outcome;
        }

        public void RemoveEntry(ParagraphKey key)
        {
            _status.TryRemove(key, out _);
            _ancestry.TryRemove(key, out _);
        }

        public void ClearOutcome(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            var removed = _outcomes.TryRemove(key, out _);
            removed |= _resolved.TryRemove(key, out _);
            if (removed) Changed?.Invoke();
        }

        public void ClearAll()
        {
            _status.Clear();
            _ancestry.Clear();
            _resolved.Clear();
        }

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            return _status.TryGetValue(key, out var s) ? s : null;
        }

        public ParagraphOutcome? OutcomeOf(ProjectFolderId folder, Guid paragraphId)
            => _outcomes.TryGetValue(new ParagraphKey(folder, paragraphId), out var o) ? o : null;

        public ResolvedCharacter? ResolvedOf(ProjectFolderId folder, Guid paragraphId)
            => _resolved.TryGetValue(new ParagraphKey(folder, paragraphId), out var r) ? r : null;

        public NodeQueueSummary SummaryForNode(ProjectFolderId folder, Guid nodeId)
        {
            bool hasProcessing = false;
            int queued = 0;
            foreach (var (key, status) in _status)
            {
                if (key.Folder != folder) continue;
                if (!_ancestry.TryGetValue(key, out var anc)) continue;
                if (anc.Chapter != nodeId && anc.Part != nodeId && anc.Volume != nodeId) continue;
                if (status == ParagraphQueueStatus.Processing) hasProcessing = true;
                else queued++;
            }
            return new NodeQueueSummary(hasProcessing, queued);
        }

        public (int queued, int processing) CountStatuses()
        {
            int q = 0, p = 0;
            foreach (var s in _status.Values)
            {
                if (s == ParagraphQueueStatus.Queued) q++;
                else p++;
            }
            return (q, p);
        }
    }
}
