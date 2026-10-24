using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services.Pronunciations;

/// <summary>
/// One span of an item's text the list matches: where it is, the entry it names, and where the
/// sentence around it starts in the item's text (what a try-out names that sentence by).
/// </summary>
public sealed record PronunciationSpan(Guid ItemId, int Start, int Length, Guid PronunciationId, int SentenceStart);

/// <summary>The spans in some chapters, and the entries they name (each once, by written form).</summary>
public sealed record PronunciationSpanReport(
    IReadOnlyList<PronunciationRef> Pronunciations, IReadOnlyList<PronunciationSpan> Spans);

/// <summary>
/// Where the list matches in the Book view's loaded chapters: the host's <see cref="PronunciationMatcher"/>
/// decides, so the web's marks never re-implement matching.
/// </summary>
public static class PronunciationSpans
{
    public static async Task<PronunciationSpanReport> ReadAsync(
        ProjectDbContext db, IReadOnlyCollection<Guid> chapterIds, CancellationToken ct)
    {
        var list = await db.Pronunciations.AsNoTracking().ToListAsync(ct);
        if (list.Count == 0 || chapterIds.Count == 0)
            return new PronunciationSpanReport([], []);

        var items = await db.ParagraphItems.AsNoTracking()
            .Where(i => i.Text != null && i.Text != "" && chapterIds.Contains(i.Paragraph.ChapterId))
            .OrderBy(i => i.Paragraph.Chapter.Part.Volume.Order)
            .ThenBy(i => i.Paragraph.Chapter.Part.Order)
            .ThenBy(i => i.Paragraph.Chapter.Order)
            .ThenBy(i => i.Paragraph.Order)
            .ThenBy(i => i.Order)
            .Select(i => new { i.Id, Text = i.Text! })
            .ToListAsync(ct);

        var matcher = new PronunciationMatcher(list);
        var spans = new List<PronunciationSpan>();
        foreach (var item in items)
        {
            var hits = matcher.MatchesIn(item.Text);
            if (hits.Count == 0)
                continue;
            var sentences = PronunciationImpact.SentencesOf(item.Text);
            spans.AddRange(hits.Select(hit => new PronunciationSpan(
                item.Id, hit.Start, hit.Length, hit.PronunciationId,
                PronunciationImpact.SentenceAround(sentences, hit, item.Text.Length).Start)));
        }

        var named = spans.Select(s => s.PronunciationId).ToHashSet();
        return new PronunciationSpanReport(
            [.. list.Where(p => named.Contains(p.Id))
                .OrderBy(p => p.WrittenForm, StringComparer.OrdinalIgnoreCase)
                .Select(p => new PronunciationRef(p.Id, p.WrittenForm, p.SpokenForm))],
            spans);
    }
}
