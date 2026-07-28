using System.Collections.Concurrent;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;

namespace Read2Me.Services.NodeStatus
{
    /// <summary>
    /// Everything a book-tree node row shows, in one value: the three counts of work still
    /// outstanding (seeded from the database, patched by the On* transitions) plus what the
    /// attribution queue currently has in flight beneath the node (probed live). The two sources
    /// and cadences differ inside <see cref="NodeStatusService"/>; the row sees one shape.
    /// </summary>
    /// <remarks>
    /// The in-flight fields are attribution-specific by name on purpose: they come from the
    /// character queue and only from it, so adding audio in-flight later is an addition, not a
    /// rename. <see cref="IsDone"/> deliberately ignores them — <em>done</em> means no work
    /// remains, not that nothing is running.
    /// </remarks>
    public readonly record struct NodeStatusSummary(
        int AttributionRemaining,
        int AudioRemaining,
        int Review,
        bool AttributionProcessing,
        int AttributionQueued)
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
        private readonly IParagraphQueueProbe _queue;

        public event Action? Changed;

        /// <summary>
        /// The probe is required, not optional: a no-op default would let a mis-wired registration
        /// render permanently empty in-flight chips and still pass every test. Both this service and
        /// the probe's implementation are singletons, so this constructor-time subscription lives for
        /// the process and never leaks.
        /// </summary>
        public NodeStatusService(IParagraphQueueProbe queue)
        {
            _queue = queue;
            _queue.Changed += () => Changed?.Invoke();
        }

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

        public void OnCharacterAttributed(ProjectFolderId folder, Guid paragraphId, int remainingUnattributed)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.Unattributed = remainingUnattributed;
            }
            Changed?.Invoke();
        }

        public void OnAudioAssigned(ProjectFolderId folder, Guid paragraphId)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.MissingAudio = Math.Max(0, status.MissingAudio - 1);
            }
            Changed?.Invoke();
        }

        public void OnReviewChanged(ProjectFolderId folder, Guid paragraphId, bool needsReview)
        {
            var key = new ParagraphKey(folder, paragraphId);
            if (_entries.TryGetValue(key, out var status))
            {
                lock (status)
                    status.Review = needsReview ? 1 : 0;
            }
            Changed?.Invoke();
        }

        public int AudioRemainingForFolder(ProjectFolderId folder)
        {
            int audio = 0;
            foreach (var (key, status) in _entries)
            {
                if (key.Folder != folder) continue;
                if (status.MissingAudio > 0) audio++;
            }
            return audio;
        }

        /// <summary>
        /// Rolls every paragraph under <paramref name="nodeId"/> up into one summary in a single
        /// pass — outstanding counts and queue in-flight state share the ancestry scan.
        /// </summary>
        /// <remarks>
        /// <b>This answers only for a seeded folder.</b> Ancestry lives here and nowhere else: the
        /// queue holds no <c>(chapter, part, volume)</c> map of its own, so a paragraph queued in a
        /// folder that was never <see cref="Seed"/>ed contributes nothing to the in-flight fields.
        /// That is not a regression — the tree renders only the folder it has just seeded, and
        /// seeding is folder-wide from the database, so it covers paragraphs a queue-side map would
        /// have missed after an edit — but it is a real narrowing and is stated here rather than
        /// left to be rediscovered.
        /// </remarks>
        public NodeStatusSummary StatusForNode(ProjectFolderId folder, Guid nodeId)
        {
            int attribution = 0;
            int audio = 0;
            int review = 0;
            bool processing = false;
            int queued = 0;

            foreach (var (key, status) in _entries)
            {
                if (key.Folder != folder) continue;
                if (status.ChapterId != nodeId && status.PartId != nodeId && status.VolumeId != nodeId) continue;

                if (status.Unattributed > 0) attribution++;
                if (status.MissingAudio > 0) audio++;
                if (status.Review > 0) review++;

                switch (_queue.StatusOf(folder, key.ParagraphId))
                {
                    case ParagraphQueueStatus.Processing: processing = true; break;
                    case ParagraphQueueStatus.Queued: queued++; break;
                }
            }

            return new NodeStatusSummary(attribution, audio, review, processing, queued);
        }
    }
}
