using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

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
            var refs = await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character)
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

        public async Task<HierarchyChildren> GetChildrenAsync(
            ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId)
        {
            var db = await _session.OpenAsync(folderId);
            return parentLevel switch
            {
                BookNodeLevel.Volume => new HierarchyChildren(
                    await db.Parts.Where(p => p.VolumeId == parentId).OrderBy(p => p.Order).ToListAsync(),
                    null, null),
                BookNodeLevel.Part => new HierarchyChildren(
                    null,
                    await db.Chapters.Where(c => c.PartId == parentId).OrderBy(c => c.Order).ToListAsync(),
                    null),
                _ => new HierarchyChildren(
                    null, null,
                    await db.Paragraphs
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
                        .Where(i => i.ItemType == ParagraphItemType.Character)
                        .OrderBy(i => i.Order)
                        .Select(i => i.Text ?? "")
                        .FirstOrDefault() ?? ""))
                .ToListAsync();
        }

        public async Task<ParagraphContext?> GetParagraphContextAsync(
            ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after)
        {
            var db = await _session.OpenAsync(folderId);

            var paragraphs = await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Select(p => new
                {
                    p.Id,
                    HasCharacterItem = p.Items.Any(i => i.ItemType == ParagraphItemType.Character),
                    HasContentItem = p.Items.Any(i => i.ItemType == ParagraphItemType.Character || i.ItemType == ParagraphItemType.Narration),
                    CharacterName = p.Items
                        .Where(i => i.ItemType == ParagraphItemType.Character && i.Character != null)
                        .Select(i => i.Character!.Name)
                        .FirstOrDefault(),
                    Text = string.Join(" ", p.Items
                        .Where(i => i.ItemType == ParagraphItemType.Character || i.ItemType == ParagraphItemType.Narration)
                        .OrderBy(i => i.Order)
                        .Select(i => i.Text ?? ""))
                })
                .ToListAsync();

            var idx = paragraphs.FindIndex(p => p.Id == paragraphId);
            if (idx < 0)
                return null;

            // CharacterId set -> known speaker name. No CharacterId + has Character item -> dialog unattributed -> null. No Character items -> narration.
            static string? ResolveSpeaker(string? characterName, bool hasCharacterItem)
                => characterName ?? (hasCharacterItem ? null : "Narrator");

            var contentParagraphs = paragraphs.Where(p => p.HasContentItem).ToList();
            var contentIdx = contentParagraphs.FindIndex(p => p.Id == paragraphId);

            int precedingStart = Math.Max(0, contentIdx - before);
            var preceding = contentParagraphs
                .GetRange(precedingStart, contentIdx - precedingStart)
                .Select(p => new ContextParagraph(p.Text, ResolveSpeaker(p.CharacterName, p.HasCharacterItem)))
                .ToList();

            int followingStart = contentIdx + 1;
            int followingCount = Math.Min(after, contentParagraphs.Count - followingStart);
            var following = followingCount > 0
                ? contentParagraphs.GetRange(followingStart, followingCount)
                    .Select(p => new ContextParagraph(p.Text, ResolveSpeaker(p.CharacterName, p.HasCharacterItem)))
                    .ToList()
                : new List<ContextParagraph>();

            var q = paragraphs[idx];
            return new ParagraphContext(
                new ContextParagraph(q.Text, ResolveSpeaker(q.CharacterName, q.HasCharacterItem)),
                preceding, following);
        }
    }
}
