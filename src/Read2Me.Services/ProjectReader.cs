using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectReader : IProjectReader
    {
        private readonly ProjectDbSession _session;
        private readonly ILogger<ProjectReader> _logger;

        public ProjectReader(ProjectDbSession session, ILogger<ProjectReader> logger)
        {
            _session = session;
            _logger = logger;
        }

        public IReadOnlyList<string> GetProjects()
        {
            var folders = _session.FileSystem.ListProjectFolders();
            _logger.LogDebug("Found {Count} project(s) in workspace", folders.Count);
            return folders;
        }

        public async Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync()
        {
            var folders = GetProjects();
            var summaries = new List<ProjectSummary>(folders.Count);
            foreach (var folder in folders)
            {
                var project = await GetProjectAsync(folder);
                summaries.Add(new ProjectSummary(folder, project?.Title ?? folder));
            }
            return summaries;
        }

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

        public async Task<ProjectEntity?> GetProjectAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_session.FileSystem.GetProjectFolderPath(folderId), "project.db");
            if (!_session.FileSystem.FileExists(dbPath))
            {
                _logger.LogWarning("GetProjectAsync: no DB found for folder '{Folder}'", folderId);
                return null;
            }

            var db = await _session.OpenAsync(folderId);
            return await db.Projects.SingleOrDefaultAsync();
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

        public async Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Characters.OrderBy(c => c.IsNarrator ? 0 : 1).ThenBy(c => c.Name).ToListAsync();
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

        public async Task<List<CharacterParagraphRef>> GetVolumeCharacterParagraphsAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character
                    && i.Paragraph.Chapter.Part.VolumeId == volumeId)
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
                    i.Paragraph.Chapter.PartId,
                    volumeId))
                .Distinct()
                .ToListAsync();
        }

        public async Task<List<CharacterParagraphRef>> GetPartCharacterParagraphsAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character
                    && i.Paragraph.Chapter.PartId == partId)
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
                    partId,
                    i.Paragraph.Chapter.Part.VolumeId))
                .Distinct()
                .ToListAsync();
        }

        public async Task<List<CharacterParagraphRef>> GetChapterCharacterParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character
                    && i.Paragraph.ChapterId == chapterId)
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    chapterId,
                    i.Paragraph.Chapter.PartId,
                    i.Paragraph.Chapter.Part.VolumeId))
                .Distinct()
                .ToListAsync();
        }

        public async Task<int> GetVolumeCharacterParagraphCountAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .Where(p => p.Chapter.Part.VolumeId == volumeId
                    && p.Items.Any(i => i.ItemType == ParagraphItemType.Character))
                .CountAsync();
        }

        public async Task<int> GetPartCharacterParagraphCountAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .Where(p => p.Chapter.PartId == partId
                    && p.Items.Any(i => i.ItemType == ParagraphItemType.Character))
                .CountAsync();
        }

        public async Task<int> GetChapterCharacterParagraphCountAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId
                    && p.Items.Any(i => i.ItemType == ParagraphItemType.Character))
                .CountAsync();
        }

        public async Task<List<CharacterParagraphRef>> GetVolumeUnprocessedCharacterParagraphsAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character
                    && i.CharacterId == null
                    && i.Paragraph.Chapter.Part.VolumeId == volumeId)
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
                    i.Paragraph.Chapter.PartId,
                    volumeId))
                .Distinct()
                .ToListAsync();
        }

        public async Task<List<CharacterParagraphRef>> GetPartUnprocessedCharacterParagraphsAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character
                    && i.CharacterId == null
                    && i.Paragraph.Chapter.PartId == partId)
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
                    partId,
                    i.Paragraph.Chapter.Part.VolumeId))
                .Distinct()
                .ToListAsync();
        }

        public async Task<List<CharacterParagraphRef>> GetChapterUnprocessedCharacterParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character
                    && i.CharacterId == null
                    && i.Paragraph.ChapterId == chapterId)
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    chapterId,
                    i.Paragraph.Chapter.PartId,
                    i.Paragraph.Chapter.Part.VolumeId))
                .Distinct()
                .ToListAsync();
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
                    p.CharacterId,
                    CharacterName = p.Character != null ? p.Character.Name : null,
                    HasCharacterItem = p.Items.Any(i => i.ItemType == ParagraphItemType.Character),
                    HasContentItem = p.Items.Any(i => i.ItemType == ParagraphItemType.Character || i.ItemType == ParagraphItemType.Narration),
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

        public async Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);

            // Chapter/Part/Volume ids of every character-bearing paragraph, in one round trip.
            var rows = await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character)
                .Select(i => new
                {
                    ChapterId = i.Paragraph.ChapterId,
                    PartId = i.Paragraph.Chapter.PartId,
                    VolumeId = i.Paragraph.Chapter.Part.VolumeId,
                })
                .Distinct()
                .ToListAsync();

            var nodes = new HashSet<Guid>();
            foreach (var r in rows)
            {
                nodes.Add(r.ChapterId);
                nodes.Add(r.PartId);
                nodes.Add(r.VolumeId);
            }
            return nodes;
        }
    }
}
