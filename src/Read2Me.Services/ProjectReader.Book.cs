using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Llm;

namespace Read2Me.Services
{
    // IBookContentReader — book structure and text.
    public partial class ProjectReader
    {
        public async Task<BookOverview> GetBookOverviewAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_session.FileSystem.GetProjectFolderPath(folderId), "project.db");
            if (!_session.FileSystem.FileExists(dbPath))
                return new BookOverview(null, false, [], [], 0, 0, [], new Dictionary<Guid, int>());

            var db = await _session.OpenAsync(folderId);
            var project = await db.Projects.SingleOrDefaultAsync();
            string? filename = project?.Filename;
            bool hasContent = await db.Volumes.AnyAsync();
            if (!hasContent)
                return new BookOverview(filename, false, [], [], 0, 0, [], new Dictionary<Guid, int>());

            var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
            var characters = await db.Characters.OrderBy(c => c.IsNarrator ? 0 : 1).ThenBy(c => c.Name).ToListAsync();
            var totalParts = await db.Parts.CountAsync();
            var totalChapters = await db.Chapters.CountAsync();

            // One query: distinct character-paragraph refs for counting and selectable-node set.
            // A Character paragraph is one with at least one non-narrator speech item (ADR-0006).
            var refs = await db.ParagraphItems
                .Where(NarrationRule.IsDialogExpression)
                .Select(i => new
                {
                    ParagraphId = i.ParagraphId,
                    ChapterId = i.Paragraph.ChapterId,
                    PartId = i.Paragraph.Chapter.PartId,
                    VolumeId = i.Paragraph.Chapter.Part.VolumeId,
                })
                .Distinct()
                .ToListAsync();

            var nodes = new HashSet<Guid>();
            var counts = new Dictionary<Guid, int>();
            foreach (var r in refs)
            {
                nodes.Add(r.ChapterId);
                nodes.Add(r.PartId);
                nodes.Add(r.VolumeId);
                counts.TryGetValue(r.ChapterId, out var c); counts[r.ChapterId] = c + 1;
                counts.TryGetValue(r.PartId, out var p); counts[r.PartId] = p + 1;
                counts.TryGetValue(r.VolumeId, out var v); counts[r.VolumeId] = v + 1;
            }

            return new BookOverview(filename, true, volumes, characters, totalParts, totalChapters, nodes, counts);
        }

        public async Task<bool> HasBookContentAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_session.FileSystem.GetProjectFolderPath(folderId), "project.db");
            if (!_session.FileSystem.FileExists(dbPath))
                return false;

            var db = await _session.OpenAsync(folderId);
            return await db.Volumes.AnyAsync();
        }

        public async Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        }

        public async Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Parts.Where(p => p.VolumeId == volumeId).OrderBy(p => p.Order).ToListAsync();
        }

        public async Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Chapters.Where(c => c.PartId == partId).OrderBy(c => c.Order).ToListAsync();
        }

        public async Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Include(p => p.Items.OrderBy(i => i.Order))
                    .ThenInclude(i => i.Character)
                .ToListAsync();
        }

        /// <summary>
        /// Reads exactly the named paragraphs, items included — the targeted read behind an exact
        /// receipt (ADR 0007). Untracked for the same reason <see cref="GetChildrenAsync"/> is: it
        /// exists to see a write made through another scope.
        /// <para>
        /// Ordered by position within each chapter, but not across them: a caller refreshing loaded
        /// rows already knows where each paragraph sits, and ordering the whole set would need the
        /// hierarchy walk this read exists to avoid.
        /// </para>
        /// </summary>
        public async Task<List<Paragraph>> GetParagraphsAsync(
            ProjectFolderId folderId, IReadOnlyCollection<Guid> paragraphIds)
        {
            if (paragraphIds.Count == 0) return [];

            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .AsNoTracking()
                .Where(p => paragraphIds.Contains(p.Id))
                .OrderBy(p => p.Order)
                .Include(p => p.Items.OrderBy(i => i.Order))
                    .ThenInclude(i => i.Character)
                .ToListAsync();
        }

        /// <summary>
        /// Reads are untracked: the session's context is long-lived, so a tracked entity would be
        /// served from the identity map on re-read and hide a write made through another scope —
        /// exactly what re-reading a paragraph after attribution rewrote its items is for.
        /// </summary>
        public async Task<HierarchyChildren> GetChildrenAsync(
            ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId)
        {
            var db = await _session.OpenAsync(folderId);
            return parentLevel switch
            {
                BookNodeLevel.Volume => new HierarchyChildren(
                    await db.Parts.AsNoTracking().Where(p => p.VolumeId == parentId).OrderBy(p => p.Order).ToListAsync(),
                    null, null),
                BookNodeLevel.Part => new HierarchyChildren(
                    null,
                    await db.Chapters.AsNoTracking().Where(c => c.PartId == parentId).OrderBy(c => c.Order).ToListAsync(),
                    null),
                _ => new HierarchyChildren(
                    null, null,
                    await db.Paragraphs
                        .AsNoTracking()
                        .Where(p => p.ChapterId == parentId)
                        .OrderBy(p => p.Order)
                        .Include(p => p.Items.OrderBy(i => i.Order))
                            .ThenInclude(i => i.Character)
                        .ToListAsync()),
            };
        }

        public async Task<int> GetTotalPartCountAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Parts.CountAsync();
        }

        public async Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Chapters.CountAsync();
        }

        public async Task<List<(Guid ParagraphId, string Preview)>> GetOrderedParagraphsAsync(
            ProjectFolderId folderId, IEnumerable<Guid> paragraphIds)
        {
            var ids = paragraphIds.ToHashSet();
            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .Where(p => ids.Contains(p.Id))
                .OrderBy(p => p.Chapter.Part.Volume.Order)
                .ThenBy(p => p.Chapter.Part.Order)
                .ThenBy(p => p.Chapter.Order)
                .ThenBy(p => p.Order)
                .Select(p => ValueTuple.Create(
                    p.Id,
                    p.Items
                        // NarrationRule's rule, spelled out — a nested collection projection cannot
                        // compose the seam's expression. Keep the two in step.
                        .Where(i => i.ItemType == ParagraphItemType.Speech
                                 && i.CharacterId != ProjectDbContext.NarratorId)
                        .OrderBy(i => i.Order)
                        .Select(i => i.Text ?? "")
                        .FirstOrDefault() ?? ""))
                .ToListAsync();
        }

        public async Task<ParagraphContext?> GetParagraphContextAsync(
            ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after)
        {
            var paragraphs = await LoadChapterContextRowsAsync(folderId, chapterId);

            var idx = paragraphs.FindIndex(p => p.Id == paragraphId);
            if (idx < 0)
                return null;

            var contentParagraphs = paragraphs.Where(p => p.HasContentItem).ToList();
            var contentIdx = contentParagraphs.FindIndex(p => p.Id == paragraphId);

            int precedingStart = Math.Max(0, contentIdx - before);
            var preceding = contentParagraphs
                .GetRange(precedingStart, contentIdx - precedingStart)
                .Select(ToContextParagraph)
                .ToList();

            int followingStart = contentIdx + 1;
            int followingCount = Math.Min(after, contentParagraphs.Count - followingStart);
            var following = followingCount > 0
                ? contentParagraphs.GetRange(followingStart, followingCount)
                    .Select(ToContextParagraph)
                    .ToList()
                : new List<ContextParagraph>();

            return new ParagraphContext(ToContextParagraph(paragraphs[idx]), preceding, following);
        }

        public async Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
            ProjectFolderId folderId, Guid chapterId, IReadOnlyList<Guid> paragraphIds, int before, int after)
        {
            if (paragraphIds.Count == 0)
                return null;

            var paragraphs = await LoadChapterContextRowsAsync(folderId, chapterId);
            var contentParagraphs = paragraphs.Where(p => p.HasContentItem).ToList();

            var firstIdx = contentParagraphs.FindIndex(p => p.Id == paragraphIds[0]);
            if (firstIdx < 0)
                return null;

            // Walk forward from the first target collecting the leading contiguous run of the
            // requested ids. Narration and fully-attributed character paragraphs are context and
            // never break the run; a paragraph with any unstamped character item that is not the
            // next requested id ends it — everything requested beyond that point is deferred.
            // "Fully attributed" means every character item stamped: a partly-attributed paragraph
            // still carries unknown segments, so it is not settled enough to sit inside a run.
            var included = new List<Guid> { paragraphIds[0] };
            var lastIncludedIdx = firstIdx;
            var nextRequested = 1;
            for (int i = firstIdx + 1; i < contentParagraphs.Count && nextRequested < paragraphIds.Count; i++)
            {
                var p = contentParagraphs[i];
                if (p.Id == paragraphIds[nextRequested])
                {
                    included.Add(p.Id);
                    lastIncludedIdx = i;
                    nextRequested++;
                }
                else if (p.HasUnattributedItem)
                {
                    break;
                }
            }
            var deferred = paragraphIds.Skip(nextRequested).ToList();

            var entries = new List<BatchContextEntry>();
            int precedingStart = Math.Max(0, firstIdx - before);
            for (int i = precedingStart; i < firstIdx; i++)
                entries.Add(ToContextEntry(contentParagraphs[i], null));

            var targetIndex = 0;
            for (int i = firstIdx; i <= lastIncludedIdx; i++)
            {
                var p = contentParagraphs[i];
                if (targetIndex < included.Count && p.Id == included[targetIndex])
                    entries.Add(ToContextEntry(p, targetIndex++));
                else
                    entries.Add(ToContextEntry(p, null));
            }

            int followingEnd = Math.Min(contentParagraphs.Count, lastIncludedIdx + 1 + after);
            for (int i = lastIncludedIdx + 1; i < followingEnd; i++)
                entries.Add(ToContextEntry(contentParagraphs[i], null));

            return new ParagraphBatchContext(entries, included, deferred);
        }

        private static ContextParagraph ToContextParagraph(ChapterContextRow row) =>
            new(row.Text, ToItems(row));

        private static BatchContextEntry ToContextEntry(ChapterContextRow row, int? targetIndex) =>
            new(row.Text, ToItems(row), targetIndex);

        // Existing items in the wire shape the LLM answers in, in Order sequence — the order the
        // query paragraph's item indices are assigned from. A character item with no stamped
        // character is the "unknown" sentinel, not a missing speaker.
        private static IReadOnlyList<ContextItem> ToItems(ChapterContextRow row) =>
            [.. row.Items.Select(i => i.IsDialog
                ? new ContextItem(i.Id, i.Text, AttributionWire.Dialog, i.CharacterName ?? AttributionWire.Unknown)
                : new ContextItem(i.Id, i.Text, AttributionWire.Narration, AttributionWire.Narrator))];

        private sealed record ChapterContextItemRow(Guid Id, bool IsDialog, string? CharacterName, string Text);

        private sealed record ChapterContextRow(Guid Id, IReadOnlyList<ChapterContextItemRow> Items)
        {
            public bool HasContentItem => Items.Count > 0;

            /// <summary>Any dialog item still without a character — the paragraph is not fully attributed.</summary>
            public bool HasUnattributedItem => Items.Any(i => i.IsDialog && i.CharacterName == null);

            public string Text => string.Join(" ", Items.Select(i => i.Text));
        }

        private async Task<List<ChapterContextRow>> LoadChapterContextRowsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);

            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Select(p => new ChapterContextRow(
                    p.Id,
                    p.Items
                        .Where(i => i.ItemType == ParagraphItemType.Speech)
                        .OrderBy(i => i.Order)
                        .Select(i => new ChapterContextItemRow(
                            i.Id,
                            // NarrationRule's rule, spelled out: a nested collection projection
                            // cannot compose the seam's expression. Keep the two in step.
                            i.CharacterId != ProjectDbContext.NarratorId,
                            i.Character != null ? i.Character.Name : null,
                            i.Text ?? ""))
                        .ToList()))
                .ToListAsync();
        }
    }
}
