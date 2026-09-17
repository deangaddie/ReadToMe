using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services.Voice;

/// <summary>One chapter of the preview: the voice a character's rules pick at its start, null when none resolves.</summary>
public sealed record ChapterVoicePreview(Guid ChapterId, string ChapterTitle, string? VoiceName);

/// <summary>
/// The resolved-voice preview behind the Voice rules section: which voice wins where, one row per
/// chapter in book order. The grain is the chapter <em>start</em> — a rule anchored inside a
/// chapter (paragraph, line) is below it and does not show; the reader's chapter voice map is where
/// per-item resolution lives.
/// </summary>
public interface IVoiceRulePreview
{
    Task<IReadOnlyList<ChapterVoicePreview>> PreviewChaptersAsync(
        ProjectFolderId folder, Guid characterId, CancellationToken ct = default);
}

public sealed class VoiceRulePreview(ProjectDbSession session) : IVoiceRulePreview
{
    public async Task<IReadOnlyList<ChapterVoicePreview>> PreviewChaptersAsync(
        ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
    {
        var db = await session.OpenAsync(folder);

        var chapters = await db.Chapters.AsNoTracking()
            .OrderBy(c => c.Part.Volume.Order).ThenBy(c => c.Part.Order).ThenBy(c => c.Order)
            .Select(c => new { c.Id, c.Title, ChOrder = c.Order, PartOrder = c.Part.Order, VolOrder = c.Part.Volume.Order })
            .ToListAsync(ct);

        var rules = await db.VoiceRules.AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderBy(r => r.Rank)
            .ToListAsync(ct);

        if (rules.Count == 0)
            return chapters.Select(c => new ChapterVoicePreview(c.Id, c.Title ?? "", null)).ToList();

        var tables = await NodeOrderTableLoader.LoadAsync(db, rules, ct);
        var inputs = rules.Select(r => AnchorSpanResolver.Build(r, tables)).ToList();

        var voiceIds = rules.Select(r => r.VoiceId).ToHashSet();
        var voiceNames = await db.Voices.AsNoTracking()
            .Where(v => voiceIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.Name ?? "", ct);

        var result = new List<ChapterVoicePreview>(chapters.Count);
        foreach (var c in chapters)
        {
            var start = new StoryPosition(c.VolOrder, c.PartOrder, c.ChOrder, StoryPosition.MinKey, StoryPosition.MinKey);
            var voiceId = VoiceRuleEvaluator.Evaluate(inputs, start);
            var name = voiceId is { } id && voiceNames.TryGetValue(id, out var n) ? n : null;
            result.Add(new ChapterVoicePreview(c.Id, c.Title ?? "", name));
        }
        return result;
    }
}
