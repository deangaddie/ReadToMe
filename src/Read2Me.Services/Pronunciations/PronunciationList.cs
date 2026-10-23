using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services.Pronunciations;

/// <summary>
/// One Pronunciation and its reach in the book: how often its written form matches, in how
/// many items, and how many of those items have audio.
/// </summary>
public sealed record PronunciationEntry(
    Guid Id, string WrittenForm, string SpokenForm, int MatchCount, int ItemCount, int WithAudioCount);

/// <summary>Reads a book's Pronunciation list with each entry's reach.</summary>
public static class PronunciationList
{
    /// <summary>The list sorted by written form, counted over every item's text with the whole list applied.</summary>
    public static async Task<IReadOnlyList<PronunciationEntry>> ReadAsync(ProjectDbContext db, CancellationToken ct)
    {
        var list = await db.Pronunciations.AsNoTracking().ToListAsync(ct);
        if (list.Count == 0)
            return [];

        var matcher = new PronunciationMatcher(list);
        var items = await db.ParagraphItems.AsNoTracking()
            .Where(i => i.Text != null && i.Text != "")
            .Select(i => new { i.Text, HasAudio = i.AudioFileName != null })
            .ToListAsync(ct);

        var reach = list.ToDictionary(p => p.Id, _ => (Matches: 0, Items: 0, WithAudio: 0));
        foreach (var item in items)
        {
            // Counted per item: the longest-wins pass decides which entry each span belongs to.
            foreach (var hits in matcher.MatchesIn(item.Text!).GroupBy(m => m.PronunciationId))
            {
                var (matches, inItems, withAudio) = reach[hits.Key];
                reach[hits.Key] = (matches + hits.Count(), inItems + 1, withAudio + (item.HasAudio ? 1 : 0));
            }
        }

        return list
            .OrderBy(p => p.WrittenForm, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PronunciationEntry(
                p.Id, p.WrittenForm, p.SpokenForm, reach[p.Id].Matches, reach[p.Id].Items, reach[p.Id].WithAudio))
            .ToList();
    }
}
