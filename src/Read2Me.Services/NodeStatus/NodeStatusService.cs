using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Read2Me.Core.Models;

namespace Read2Me.Services.NodeStatus
{
    public readonly record struct NodeStatusSummary(int AttributionRemaining, int AudioRemaining, int Review)
    {
        public bool IsDone => AttributionRemaining == 0 && AudioRemaining == 0 && Review == 0;
    }

    public readonly record struct ParagraphStatusSeedRow(
        Guid ParagraphId,
        Guid ChapterId,
        Guid PartId,
        Guid VolumeId,
        int Unattributed,
        int MissingAudio,
        int Review);

    public sealed class NodeStatusService
    {
        private readonly record struct ParagraphKey(ProjectFolderId Folder, Guid ParagraphId);

        private sealed class ParagraphStatus
        {
            public int Unattributed;
            public int MissingAudio;
            public int Review;
            public Guid ChapterId;
            public Guid PartId;
            public Guid VolumeId;
        }

        private readonly ConcurrentDictionary<ParagraphKey, ParagraphStatus> _entries = new();

        public event Action? Changed;

        public void Seed(ProjectFolderId folder, IEnumerable<ParagraphStatusSeedRow> rows)
        {
            // Remove existing entries for this folder.
            foreach (var key in _entries.Keys)
                if (key.Folder == folder) _entries.TryRemove(key, out _);

            foreach (var row in rows)
            {
                var key = new ParagraphKey(folder, row.ParagraphId);
                _entries[key] = new ParagraphStatus
                {
                    Unattributed = row.Unattributed,
                    MissingAudio = row.MissingAudio,
                    Review = row.Review,
                    ChapterId = row.ChapterId,
                    PartId = row.PartId,
                    VolumeId = row.VolumeId,
                };
            }

            Changed?.Invoke();
        }

        public void Clear(ProjectFolderId folder)
        {
            foreach (var key in _entries.Keys)
                if (key.Folder == folder) _entries.TryRemove(key, out _);

            Changed?.Invoke();
        }

        public void DecrementUnattributed(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.Unattributed = Math.Max(0, status.Unattributed - 1);
            }
            Changed?.Invoke();
        }

        public void DecrementMissingAudio(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.MissingAudio = Math.Max(0, status.MissingAudio - 1);
            }
            Changed?.Invoke();
        }

        public void ZeroParagraphAttribution(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.Unattributed = 0;
            }
            Changed?.Invoke();
        }

        public void SetParagraphReview(ProjectFolderId folder, Guid paragraphId, bool needsReview)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.Review = needsReview ? 1 : 0;
            }
            Changed?.Invoke();
        }

        public NodeStatusSummary StatusForNode(ProjectFolderId folder, Guid nodeId)
        {
            int attribution = 0;
            int audio = 0;
            int review = 0;

            foreach (var (key, status) in _entries)
            {
                if (key.Folder != folder) continue;
                if (status.ChapterId != nodeId && status.PartId != nodeId && status.VolumeId != nodeId) continue;

                if (status.Unattributed > 0) attribution++;
                if (status.MissingAudio > 0) audio++;
                if (status.Review > 0) review++;
            }

            return new NodeStatusSummary(attribution, audio, review);
        }
    }
}
