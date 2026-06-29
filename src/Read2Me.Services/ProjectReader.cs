using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Voice;
using VoiceEntity = Read2Me.Data.Entities.Voice;
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

        public async Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Characters.OrderBy(c => c.IsNarrator ? 0 : 1).ThenBy(c => c.Name).ToListAsync();
        }

        public async Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Characters
                .Include(c => c.Aliases)
                .Include(c => c.Voices)
                .OrderBy(c => c.IsNarrator ? 0 : 1)
                .ThenBy(c => c.Name)
                .ToListAsync();
        }

        public async Task<List<VoiceEntity>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Voices
                .Where(v => v.CharacterId == characterId)
                .OrderBy(v => v.CreatedUtc)
                .ToListAsync();
        }

        public async Task<Guid?> GetDefaultVoiceIdAsync(ProjectFolderId folderId, Guid characterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.VoiceRules
                .Where(r => r.CharacterId == characterId && r.IsDefault)
                .Select(r => (Guid?)r.VoiceId)
                .FirstOrDefaultAsync();
        }

        public async Task<List<VoiceRuleRow>> GetCharacterVoiceRulesAsync(ProjectFolderId folderId, Guid characterId)
        {
            var db = await _session.OpenAsync(folderId);

            var rules = await db.VoiceRules
                .AsNoTracking()
                .Where(r => r.CharacterId == characterId)
                .OrderBy(r => r.Rank)
                .ToListAsync();

            if (rules.Count == 0) return [];

            // Collect anchor node ids for batch lookup.
            var volumeIds    = new HashSet<Guid>();
            var partIds      = new HashSet<Guid>();
            var chapterIds   = new HashSet<Guid>();
            var paragraphIds = new HashSet<Guid>();
            var itemIds      = new HashSet<Guid>();
            var voiceIds     = new HashSet<Guid>();

            foreach (var r in rules)
            {
                voiceIds.Add(r.VoiceId);
                CollectId(r.FromLevel, r.FromNodeId, volumeIds, partIds, chapterIds, paragraphIds, itemIds);
                CollectId(r.ToLevel,   r.ToNodeId,   volumeIds, partIds, chapterIds, paragraphIds, itemIds);
            }

            // Load voice names.
            var voiceNames = await db.Voices
                .AsNoTracking()
                .Where(v => voiceIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Name })
                .ToDictionaryAsync(v => v.Id, v => v.Name ?? "");

            // Load node display names by level.
            Dictionary<Guid, string> volumeNames    = new();
            Dictionary<Guid, string> partNames      = new();
            Dictionary<Guid, string> chapterNames   = new();
            Dictionary<Guid, string> paragraphNames = new();
            Dictionary<Guid, string> itemNames      = new();

            if (volumeIds.Count > 0)
            {
                var rows = await db.Volumes.AsNoTracking()
                    .Where(v => volumeIds.Contains(v.Id))
                    .Select(v => new { v.Id, Name = v.Title ?? "Untitled" })
                    .ToListAsync();
                foreach (var r in rows) volumeNames[r.Id] = r.Name;
            }
            if (partIds.Count > 0)
            {
                var rows = await db.Parts.AsNoTracking()
                    .Where(p => partIds.Contains(p.Id))
                    .Select(p => new { p.Id, Name = p.Title ?? "Untitled" })
                    .ToListAsync();
                foreach (var r in rows) partNames[r.Id] = r.Name;
            }
            if (chapterIds.Count > 0)
            {
                var rows = await db.Chapters.AsNoTracking()
                    .Where(c => chapterIds.Contains(c.Id))
                    .Select(c => new { c.Id, Name = c.Title ?? "Untitled" })
                    .ToListAsync();
                foreach (var r in rows) chapterNames[r.Id] = r.Name;
            }
            if (paragraphIds.Count > 0)
            {
                var rows = await db.Paragraphs.AsNoTracking()
                    .Where(p => paragraphIds.Contains(p.Id))
                    .Select(p => new { p.Id, Name = "#" + (p.Order ?? "") })
                    .ToListAsync();
                foreach (var r in rows) paragraphNames[r.Id] = r.Name;
            }
            if (itemIds.Count > 0)
            {
                var rows = await db.ParagraphItems.AsNoTracking()
                    .Where(pi => itemIds.Contains(pi.Id))
                    .Select(pi => new { pi.Id, Name = pi.Text != null ? pi.Text.Substring(0, Math.Min(pi.Text.Length, 30)) : "" })
                    .ToListAsync();
                foreach (var r in rows) itemNames[r.Id] = "\"" + r.Name + (r.Name.Length == 30 ? "…" : "") + "\"";
            }

            string? ResolveDisplayName(VoiceAnchorLevel? level, Guid? nodeId, out bool dangling)
            {
                dangling = false;
                if (level is null || nodeId is null) return null;
                var id = nodeId.Value;
                bool found;
                string? name;
                switch (level.Value)
                {
                    case VoiceAnchorLevel.Volume:
                        found = volumeNames.TryGetValue(id, out name);
                        break;
                    case VoiceAnchorLevel.Part:
                        found = partNames.TryGetValue(id, out name);
                        break;
                    case VoiceAnchorLevel.Chapter:
                        found = chapterNames.TryGetValue(id, out name);
                        break;
                    case VoiceAnchorLevel.Paragraph:
                        found = paragraphNames.TryGetValue(id, out name);
                        break;
                    case VoiceAnchorLevel.ParagraphItem:
                        found = itemNames.TryGetValue(id, out name);
                        break;
                    default:
                        found = false; name = null;
                        break;
                }
                if (!found) { dangling = true; return null; }
                return name;
            }

            var result = new List<VoiceRuleRow>(rules.Count);
            foreach (var r in rules)
            {
                voiceNames.TryGetValue(r.VoiceId, out var voiceName);
                var fromDisplay = ResolveDisplayName(r.FromLevel, r.FromNodeId, out var fromDangling);
                var toDisplay   = ResolveDisplayName(r.ToLevel,   r.ToNodeId,   out var toDangling);
                result.Add(new VoiceRuleRow(
                    r.Id, r.IsDefault, r.Rank,
                    r.VoiceId, voiceName ?? "",
                    r.FromLevel, r.FromNodeId, fromDisplay, fromDangling,
                    r.ToLevel, r.ToNodeId, toDisplay, toDangling));
            }
            return result;
        }

        private static void CollectId(
            VoiceAnchorLevel? level, Guid? nodeId,
            HashSet<Guid> volumeIds, HashSet<Guid> partIds, HashSet<Guid> chapterIds,
            HashSet<Guid> paragraphIds, HashSet<Guid> itemIds)
        {
            if (level is null || nodeId is null) return;
            switch (level.Value)
            {
                case VoiceAnchorLevel.Volume:       volumeIds.Add(nodeId.Value);    break;
                case VoiceAnchorLevel.Part:         partIds.Add(nodeId.Value);      break;
                case VoiceAnchorLevel.Chapter:      chapterIds.Add(nodeId.Value);   break;
                case VoiceAnchorLevel.Paragraph:    paragraphIds.Add(nodeId.Value); break;
                case VoiceAnchorLevel.ParagraphItem: itemIds.Add(nodeId.Value);     break;
            }
        }

        public async Task<List<CharacterLine>> GetCharacterLinesAsync(ProjectFolderId folderId, Guid characterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character && i.CharacterId == characterId)
                .OrderBy(i => i.Paragraph.Chapter.Part.Volume.Order)
                .ThenBy(i => i.Paragraph.Chapter.Part.Order)
                .ThenBy(i => i.Paragraph.Chapter.Order)
                .ThenBy(i => i.Paragraph.Order)
                .ThenBy(i => i.Order)
                .Select(i => new CharacterLine(i.Id, i.ParagraphId, i.Paragraph.ChapterId, i.Text ?? string.Empty))
                .ToListAsync();
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

        public async Task<List<CharacterParagraphRef>> GetCharacterParagraphsAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = false)
        {
            var db = await _session.OpenAsync(folderId);

            IQueryable<Data.Entities.ParagraphItem> q = db.ParagraphItems
                .Where(i => i.ItemType == ParagraphItemType.Character);

            q = level switch
            {
                BookNodeLevel.Volume  => q.Where(i => i.Paragraph.Chapter.Part.VolumeId == nodeId),
                BookNodeLevel.Part    => q.Where(i => i.Paragraph.Chapter.PartId == nodeId),
                _                     => q.Where(i => i.Paragraph.ChapterId == nodeId),
            };

            if (unprocessedOnly)
                q = q.Where(i => i.CharacterId == null);

            return await q
                .Select(i => new CharacterParagraphRef(
                    i.ParagraphId,
                    i.Paragraph.ChapterId,
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
