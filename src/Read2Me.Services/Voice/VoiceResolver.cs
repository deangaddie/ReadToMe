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

        // 5-6. Resolve every rule's anchor to its order key (one query per anchor level in use).
        var tables = await NodeOrderTableLoader.LoadAsync(db, allRules, ct);

        // 7. Build item StoryPositions from step-1 rows (no second query)
        var itemPositions = new Dictionary<Guid, StoryPosition>(items.Count);
        foreach (var r in items)
            itemPositions[r.Id] = new StoryPosition(r.VolumeOrder, r.PartOrder, r.ChOrder, r.ParaOrder, r.ItemOrder);

        // Build RuleInputs per character (shared across items of same char)
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
}
