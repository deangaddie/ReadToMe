using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Voice;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services
{
    // ICharacterReader — characters, aliases, voices, voice rules, attribution queries.
    public partial class ProjectReader
    {
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

        public async Task<VoiceEntity?> GetVoiceAsync(ProjectFolderId folderId, Guid voiceId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Voices.FirstOrDefaultAsync(v => v.Id == voiceId);
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

        public async Task<int> CountUnattributedCharacterItemsAsync(ProjectFolderId folderId, Guid paragraphId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.ParagraphItems.CountAsync(i =>
                i.ParagraphId == paragraphId &&
                i.ItemType == ParagraphItemType.Character &&
                i.CharacterId == null);
        }

        public async Task<BulkAssignPreview> GetBulkAssignPreviewAsync(
            ProjectFolderId folderId, IReadOnlyList<Guid> paragraphIds, CancellationToken ct = default)
        {
            if (paragraphIds.Count == 0) return new BulkAssignPreview(0, 0);

            var db = await _session.OpenAsync(folderId);

            // One round trip: item count per paragraph that has at least one Character item.
            // EF renders Contains as IN (SELECT value FROM json_each(@ids)) — one parameter at any length.
            var perParagraph = await db.ParagraphItems
                .Where(i => paragraphIds.Contains(i.ParagraphId) && i.ItemType == ParagraphItemType.Character)
                .GroupBy(i => i.ParagraphId)
                .Select(g => g.Count())
                .ToListAsync(ct);

            return new BulkAssignPreview(perParagraph.Count, perParagraph.Sum());
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
