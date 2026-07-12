using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio;
using Read2Me.Services.NodeStatus;

namespace Read2Me.Services
{
    // IAudioItemReader — audio-generation state: item refs, reviews, status seeds, assembly manifest.
    public partial class ProjectReader
    {
        public async Task<List<AudioItemRef>> GetAudioItemRefsAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool needsAudioOnly = false, bool narratorOnlyMode = false)
        {
            var db = await _session.OpenAsync(folderId);

            IQueryable<Data.Entities.ParagraphItem> q = db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character || i.ItemType == ParagraphItemType.Narration);

            q = level switch
            {
                BookNodeLevel.Volume  => q.Where(i => i.Paragraph.Chapter.Part.VolumeId == nodeId),
                BookNodeLevel.Part    => q.Where(i => i.Paragraph.Chapter.PartId == nodeId),
                _                     => q.Where(i => i.Paragraph.ChapterId == nodeId),
            };

            if (needsAudioOnly)
                q = narratorOnlyMode
                    ? q.Where(i => i.AudioFileName == null)
                    : q.Where(i => i.AudioFileName == null
                                   && (i.ItemType == ParagraphItemType.Narration || i.CharacterId != null));

            return await q
                .Select(i => new AudioItemRef(
                    i.Id,
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
                    i.Paragraph.Chapter.PartId,
                    i.Paragraph.Chapter.Part.VolumeId))
                .ToListAsync();
        }

        public async Task<List<AudioItemRef>> GetOrderedAudioItemRefsAsync(
            ProjectFolderId folderId, IEnumerable<Guid> paragraphItemIds)
        {
            var ids = paragraphItemIds.ToHashSet();
            var db = await _session.OpenAsync(folderId);

            return await db.ParagraphItems
                .Where(i => ids.Contains(i.Id))
                .OrderBy(i => i.Paragraph.Chapter.Part.Volume.Order)
                .ThenBy(i => i.Paragraph.Chapter.Part.Order)
                .ThenBy(i => i.Paragraph.Chapter.Order)
                .ThenBy(i => i.Paragraph.Order)
                .ThenBy(i => i.Order)
                .Select(i => new AudioItemRef(
                    i.Id,
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
                    i.Paragraph.Chapter.PartId,
                    i.Paragraph.Chapter.Part.VolumeId))
                .ToListAsync();
        }

        public async Task<IReadOnlyList<AudioSampleInfo>> GetAudioSampleInfosAsync(
            ProjectFolderId folderId, IReadOnlyCollection<Guid> itemIds)
        {
            if (itemIds.Count == 0) return [];

            var ids = itemIds.ToHashSet();
            var db = await _session.OpenAsync(folderId);

            return await db.ParagraphItems
                .AsNoTracking()
                .Where(i => ids.Contains(i.Id) && i.AudioFileName != null)
                .Select(i => new AudioSampleInfo(
                    i.Id,
                    i.Text ?? "",
                    i.Character != null ? i.Character.Name : null,
                    i.AudioFileName!))
                .ToListAsync();
        }

        public async Task<List<(Guid ParagraphItemId, AudioReviewInfo Info)>> GetAudioReviewsAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);

            // Rows are sparse (present iff an item needs review), so a single unscoped query is cheap.
            var rows = await db.AudioReviews
                .Select(r => new
                {
                    r.ParagraphItemId,
                    r.State,
                    r.NormalizeOk,
                    r.NormalizeReason,
                    r.VerifyOk,
                    r.Wer,
                    r.VerifyReason,
                    r.Transcript,
                    r.OriginalTextSnapshot,
                })
                .ToListAsync();

            return rows
                .Select(r => (r.ParagraphItemId, new AudioReviewInfo(
                    r.State == Data.Enums.AudioReviewState.Dismissed
                        ? Core.Models.AudioReviewState.Dismissed
                        : Core.Models.AudioReviewState.NeedsReview,
                    r.NormalizeOk,
                    r.NormalizeReason,
                    r.VerifyOk,
                    r.Wer,
                    r.VerifyReason,
                    r.Transcript,
                    r.OriginalTextSnapshot)))
                .ToList();
        }

        public async Task<IReadOnlyList<ParagraphStatusSeedRow>> GetNodeStatusSeedAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);

            // One row per paragraph that has at least one non-Pause item.
            var rows = await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character || i.ItemType == ParagraphItemType.Narration)
                .GroupBy(i => new
                {
                    ParagraphId = i.ParagraphId,
                    ChapterId = i.Paragraph.ChapterId,
                    PartId = i.Paragraph.Chapter.PartId,
                    VolumeId = i.Paragraph.Chapter.Part.VolumeId,
                })
                .Select(g => new
                {
                    g.Key.ParagraphId,
                    g.Key.ChapterId,
                    g.Key.PartId,
                    g.Key.VolumeId,
                    Unattributed = g.Count(i => i.ItemType == ParagraphItemType.Character && i.CharacterId == null),
                    MissingAudio = g.Count(i => i.AudioFileName == null),
                })
                .ToListAsync();

            // Paragraph qualifies for Review=1 if any of its items has a NeedsReview AudioReview row.
            var needsReviewParagraphIds = await db.AudioReviews
                .Where(r => r.State == Data.Enums.AudioReviewState.NeedsReview)
                .Join(db.ParagraphItems,
                    r => r.ParagraphItemId,
                    i => i.Id,
                    (r, i) => i.ParagraphId)
                .Distinct()
                .ToHashSetAsync();

            return rows
                .Select(r => new ParagraphStatusSeedRow(
                    r.ParagraphId, r.ChapterId, r.PartId, r.VolumeId,
                    r.Unattributed, r.MissingAudio,
                    Review: needsReviewParagraphIds.Contains(r.ParagraphId) ? 1 : 0))
                .ToList();
        }

        public async Task<IReadOnlyDictionary<Guid, int>> GetNodeAudioItemCountsAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);

            var rows = await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character || i.ItemType == ParagraphItemType.Narration)
                .Select(i => new
                {
                    ItemId = i.Id,
                    ChapterId = i.Paragraph.ChapterId,
                    PartId = i.Paragraph.Chapter.PartId,
                    VolumeId = i.Paragraph.Chapter.Part.VolumeId,
                })
                .ToListAsync();

            var counts = new Dictionary<Guid, int>();
            foreach (var r in rows)
            {
                counts.TryGetValue(r.ChapterId, out var c); counts[r.ChapterId] = c + 1;
                counts.TryGetValue(r.PartId,    out var p); counts[r.PartId]    = p + 1;
                counts.TryGetValue(r.VolumeId,  out var v); counts[r.VolumeId]  = v + 1;
            }
            return counts;
        }

        public async Task<IReadOnlyList<AssemblyManifestEntry>> GetAssemblyManifestAsync(
            ProjectFolderId folder, CancellationToken ct)
        {
            var db = await _session.OpenAsync(folder);

            return await db.ParagraphItems
                .AsNoTracking()
                .OrderBy(i => i.Paragraph.Chapter.Part.Volume.Order)
                .ThenBy(i => i.Paragraph.Chapter.Part.Order)
                .ThenBy(i => i.Paragraph.Chapter.Order)
                .ThenBy(i => i.Paragraph.Order)
                .ThenBy(i => i.Order)
                .Select(i => new AssemblyManifestEntry(
                    i.Id,
                    i.ItemType,
                    i.ItemType == ParagraphItemType.VolumePause
                        || i.ItemType == ParagraphItemType.PartPause
                        || i.ItemType == ParagraphItemType.ChapterPause
                        || i.ItemType == ParagraphItemType.ParagraphPause
                        || i.ItemType == ParagraphItemType.Pause
                        ? null
                        : i.AudioFileName,
                    i.Paragraph.Chapter.Part.VolumeId,
                    i.Paragraph.Chapter.Part.Volume.Title,
                    i.Paragraph.Chapter.PartId,
                    i.Paragraph.Chapter.Part.Title,
                    i.Paragraph.ChapterId,
                    i.Paragraph.Chapter.Title))
                .ToListAsync(ct);
        }
    }
}
