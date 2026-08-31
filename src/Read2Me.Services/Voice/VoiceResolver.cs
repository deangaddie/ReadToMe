using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;


namespace Read2Me.Services.Voice;

public sealed class VoiceResolver : IVoiceResolver
{
    private readonly ProjectDbSession _session;

    public VoiceResolver(ProjectDbSession session)
    {
        _session = session;
    }

    public async Task<IReadOnlyDictionary<Guid, Guid?>> ResolveAsync(
        ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0)
            return new Dictionary<Guid, Guid?>();

        var db = await _session.OpenAsync(folder);

        // 1. Load items with ancestry orders (single query replaces former step 7)
        var items = await db.ParagraphItems.AsNoTracking()
            .Where(pi => itemIds.Contains(pi.Id))
            .Select(pi => new {
                pi.Id,
                pi.ItemType,
                pi.CharacterId,
                ItemOrder   = pi.Order,
                ParaOrder   = pi.Paragraph.Order,
                ChOrder     = pi.Paragraph.Chapter.Order,
                PartOrder   = pi.Paragraph.Chapter.Part.Order,
                VolumeOrder = pi.Paragraph.Chapter.Part.Volume.Order
            })
            .ToListAsync(ct);

        // 2. Read NarratorOnlyMode and the narrator link — one round-trip for both
        var (narrator, narratorOnlyMode) = await NarratorIdentity.LoadWithNarratorOnlyModeAsync(db, ct);

        // 3. Effective character per item. The speaker decides, not the item type: narration is
        //    a speaker (ADR-0006), so a sentinel-stamped item resolves through the narrator and a
        //    character-stamped one through that character's own rules — including when that
        //    character is the linked narrator, which stays a distinct choice. Unlinked,
        //    NarratorIdentity.CharacterId *is* the seed Narrator row. A pause speaks for nobody.
        var itemToCharId = new Dictionary<Guid, Guid>(items.Count);

        foreach (var it in items)
        {
            Guid? charId;
            if (ParagraphItemKinds.IsPause(it.ItemType))
                charId = null;
            else if (narratorOnlyMode || NarrationRule.IsNarration(it.CharacterId))
                charId = narrator.CharacterId;
            else
                charId = it.CharacterId;

            if (charId.HasValue)
                itemToCharId[it.Id] = charId.Value;
        }

        if (itemToCharId.Count == 0)
        {
            var empty = new Dictionary<Guid, Guid?>();
            foreach (var id in itemIds) empty[id] = null;
            return empty;
        }

        // 4. Load all effective characters' rules in ONE query, group client-side
        var effectiveCharIds = itemToCharId.Values.ToHashSet();
        var allRules = await db.VoiceRules.AsNoTracking()
            .Where(r => effectiveCharIds.Contains(r.CharacterId))
            .OrderBy(r => r.Rank)
            .ToListAsync(ct);

        var rulesByChar = allRules.GroupBy(r => r.CharacterId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 5. Collect all anchor node ids across ALL rules (global batch)
        var volumeIds    = new HashSet<Guid>();
        var partIds      = new HashSet<Guid>();
        var chapterIds   = new HashSet<Guid>();
        var paragraphIds = new HashSet<Guid>();
        var anchorItemIds = new HashSet<Guid>();

        foreach (var rule in allRules)
        {
            CollectId(rule.FromLevel, rule.FromNodeId, volumeIds, partIds, chapterIds, paragraphIds, anchorItemIds);
            CollectId(rule.ToLevel,   rule.ToNodeId,   volumeIds, partIds, chapterIds, paragraphIds, anchorItemIds);
        }

        // 6. Resolve anchor node orders in 5 level-queries
        var volOrders = volumeIds.Count > 0
            ? await db.Volumes.AsNoTracking()
                .Where(v => volumeIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Order })
                .ToDictionaryAsync(v => v.Id, v => v.Order, ct)
            : new Dictionary<Guid, string>();

        Dictionary<Guid, (string VolOrder, string PartOrder)> partOrders = new();
        if (partIds.Count > 0)
        {
            var rows = await db.Parts.AsNoTracking()
                .Where(p => partIds.Contains(p.Id))
                .Select(p => new { p.Id, PartOrder = p.Order, VolumeOrder = p.Volume.Order })
                .ToListAsync(ct);
            foreach (var r in rows) partOrders[r.Id] = (r.VolumeOrder, r.PartOrder);
        }

        Dictionary<Guid, (string VolOrder, string PartOrder, string ChOrder)> chapterOrders = new();
        if (chapterIds.Count > 0)
        {
            var rows = await db.Chapters.AsNoTracking()
                .Where(c => chapterIds.Contains(c.Id))
                .Select(c => new { c.Id, ChOrder = c.Order, PartOrder = c.Part.Order, VolumeOrder = c.Part.Volume.Order })
                .ToListAsync(ct);
            foreach (var r in rows) chapterOrders[r.Id] = (r.VolumeOrder, r.PartOrder, r.ChOrder);
        }

        Dictionary<Guid, (string VolOrder, string PartOrder, string ChOrder, string ParaOrder)> paraOrders = new();
        if (paragraphIds.Count > 0)
        {
            var rows = await db.Paragraphs.AsNoTracking()
                .Where(p => paragraphIds.Contains(p.Id))
                .Select(p => new { p.Id, ParaOrder = p.Order, ChOrder = p.Chapter.Order, PartOrder = p.Chapter.Part.Order, VolumeOrder = p.Chapter.Part.Volume.Order })
                .ToListAsync(ct);
            foreach (var r in rows) paraOrders[r.Id] = (r.VolumeOrder, r.PartOrder, r.ChOrder, r.ParaOrder);
        }

        Dictionary<Guid, StoryPosition> anchorItemPositions = new();
        if (anchorItemIds.Count > 0)
        {
            var rows = await db.ParagraphItems.AsNoTracking()
                .Where(pi => anchorItemIds.Contains(pi.Id))
                .Select(pi => new
                {
                    pi.Id,
                    ItemOrder  = pi.Order,
                    ParaOrder  = pi.Paragraph.Order,
                    ChOrder    = pi.Paragraph.Chapter.Order,
                    PartOrder  = pi.Paragraph.Chapter.Part.Order,
                    VolumeOrder = pi.Paragraph.Chapter.Part.Volume.Order
                })
                .ToListAsync(ct);
            foreach (var r in rows)
                anchorItemPositions[r.Id] = new StoryPosition(r.VolumeOrder, r.PartOrder, r.ChOrder, r.ParaOrder, r.ItemOrder);
        }

        // 7. Build item StoryPositions from step-1 rows (no second query)
        var itemPositions = new Dictionary<Guid, StoryPosition>(items.Count);
        foreach (var r in items)
            itemPositions[r.Id] = new StoryPosition(r.VolumeOrder, r.PartOrder, r.ChOrder, r.ParaOrder, r.ItemOrder);

        // Build RuleInputs per character (shared across items of same char)
        var tables = new NodeOrderTables(volOrders, partOrders, chapterOrders, paraOrders, anchorItemPositions);
        var ruleInputsByChar = new Dictionary<Guid, List<RuleInput>>();
        foreach (var (charId, charRules) in rulesByChar)
        {
            var inputs = new List<RuleInput>(charRules.Count);
            foreach (var rule in charRules)
                inputs.Add(AnchorSpanResolver.Build(rule, tables));
            ruleInputsByChar[charId] = inputs;
        }

        // 8. Evaluate each item
        var result = new Dictionary<Guid, Guid?>();
        foreach (var itemId in itemIds)
        {
            if (!itemToCharId.TryGetValue(itemId, out var charId))
            {
                result[itemId] = null;
                continue;
            }

            if (!ruleInputsByChar.TryGetValue(charId, out var ruleInputs) ||
                !itemPositions.TryGetValue(itemId, out var pos))
            {
                result[itemId] = null;
                continue;
            }

            result[itemId] = VoiceRuleEvaluator.Evaluate(ruleInputs, pos);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string?>> ResolveNamesAsync(
        ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(folder, itemIds, ct);

        var distinctVoiceIds = resolved.Values
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToHashSet();

        Dictionary<Guid, string> voiceNames = new();
        if (distinctVoiceIds.Count > 0)
        {
            var db = await _session.OpenAsync(folder);
            var rows = await db.Voices.AsNoTracking()
                .Where(v => distinctVoiceIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Name })
                .ToListAsync(ct);
            foreach (var r in rows)
                voiceNames[r.Id] = r.Name ?? "";
        }

        var result = new Dictionary<Guid, string?>(resolved.Count);
        foreach (var (itemId, voiceId) in resolved)
        {
            result[itemId] = voiceId.HasValue && voiceNames.TryGetValue(voiceId.Value, out var name)
                ? name
                : null;
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
            case VoiceAnchorLevel.Volume:        volumeIds.Add(nodeId.Value);    break;
            case VoiceAnchorLevel.Part:          partIds.Add(nodeId.Value);      break;
            case VoiceAnchorLevel.Chapter:       chapterIds.Add(nodeId.Value);   break;
            case VoiceAnchorLevel.Paragraph:     paragraphIds.Add(nodeId.Value); break;
            case VoiceAnchorLevel.ParagraphItem: itemIds.Add(nodeId.Value);      break;
        }
    }
}
