using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Audio.ParagraphTts;

namespace Read2Me.Services.Pronunciations;

/// <summary>An entry of the list, as the impact of a draft names it.</summary>
public sealed record PronunciationRef(Guid Id, string WrittenForm, string SpokenForm);

/// <summary>
/// An entry whose written form holds the draft's as whole words, or is held by it. The longer one wins
/// where both match, so <paramref name="DraftWins"/> is whether the draft's is the longer.
/// </summary>
public sealed record PronunciationOverlap(Guid Id, string WrittenForm, string SpokenForm, bool DraftWins);

/// <summary>
/// One place the draft's written form matches: the item, where it is, and the sentence around it with
/// the match's offsets in that sentence, and the sentence's Spoken text with the drafted list applied.
/// </summary>
public sealed record PronunciationMatchInBook(
    Guid ItemId, Guid ChapterId, string? ChapterTitle, string? Speaker, bool HasAudio,
    string Sentence, int MatchStart, int MatchLength, string SpokenSentence);

/// <summary>
/// What saving a draft would do: its reach (matches, items, items with audio — for a delete, the
/// entry's reach now), the audio a save discards, the entries it overlaps, the entry it duplicates,
/// and every match in book order.
/// </summary>
public sealed record PronunciationImpactReport(
    int MatchCount, int ItemCount, int WithAudioCount, int AudioDiscardCount,
    IReadOnlyList<PronunciationOverlap> Overlaps, PronunciationRef? DuplicateOf,
    IReadOnlyList<PronunciationMatchInBook> Matches);

/// <summary>
/// The dry run of a list change. It drafts the list as <see cref="Mutations.SetPronunciationMutation"/>
/// does and counts the discard with the same <see cref="DraftedList.ChangedSpokenText"/>, so a save
/// then discards exactly what this says.
/// </summary>
public static class PronunciationImpact
{
    public static async Task<PronunciationImpactReport> ReadAsync(
        ProjectDbContext db, PronunciationDraft draft, CancellationToken ct)
    {
        var drafted = draft.ApplyTo(await db.Pronunciations.AsNoTracking().ToListAsync(ct));
        var items = await db.ParagraphItems.AsNoTracking()
            .Where(i => i.Text != null && i.Text != "")
            .OrderBy(i => i.Paragraph.Chapter.Part.Volume.Order)
            .ThenBy(i => i.Paragraph.Chapter.Part.Order)
            .ThenBy(i => i.Paragraph.Chapter.Order)
            .ThenBy(i => i.Paragraph.Order)
            .ThenBy(i => i.Order)
            .Select(i => new
            {
                i.Id, Text = i.Text!, HasAudio = i.AudioFileName != null && i.AudioFileName != "",
                i.Paragraph.ChapterId, ChapterTitle = i.Paragraph.Chapter.Title, Speaker = i.Character!.Name,
            })
            .ToListAsync(ct);

        // A deleted entry's reach is where it matches now; the sentence is spoken as it would be without it.
        var matcher = new PronunciationMatcher(drafted.WithEntry);
        var spoken = new PronunciationMatcher(drafted.After);
        var matches = new List<PronunciationMatchInBook>();
        var itemCount = 0;
        var withAudio = 0;
        foreach (var item in items)
        {
            var hits = matcher.MatchesIn(item.Text).Where(m => m.PronunciationId == drafted.Entry.Id).ToList();
            if (hits.Count == 0)
                continue;
            itemCount++;
            withAudio += item.HasAudio ? 1 : 0;
            var sentences = SentencesOf(item.Text);
            foreach (var hit in hits)
            {
                var (start, length) = SentenceAround(sentences, hit, item.Text.Length);
                var sentence = item.Text.Substring(start, length);
                matches.Add(new PronunciationMatchInBook(
                    item.Id, item.ChapterId, item.ChapterTitle, item.Speaker, item.HasAudio,
                    sentence, hit.Start - start, hit.Length, spoken.SpokenTextOf(sentence)));
            }
        }

        var discarded = drafted.ChangedSpokenText(items.Where(i => i.HasAudio).Select(i => (i.Id, i.Text)));
        return new PronunciationImpactReport(
            matches.Count, itemCount, withAudio, discarded.Count,
            OverlapsOf(drafted), drafted.DuplicateOf is { } d ? new PronunciationRef(d.Id, d.WrittenForm, d.SpokenForm) : null,
            matches);
    }

    private static List<PronunciationOverlap> OverlapsOf(DraftedList drafted)
    {
        var entry = new PronunciationMatcher([drafted.Entry]);
        return [.. drafted.WithEntry
            .Where(p => p.Id != drafted.Entry.Id)
            .Select(p => entry.MatchesIn(p.WrittenForm).Count > 0 ? new PronunciationOverlap(p.Id, p.WrittenForm, p.SpokenForm, DraftWins: false)
                : new PronunciationMatcher([p]).MatchesIn(drafted.Entry.WrittenForm).Count > 0 ? new PronunciationOverlap(p.Id, p.WrittenForm, p.SpokenForm, DraftWins: true)
                : null)
            .OfType<PronunciationOverlap>()
            .OrderBy(o => o.WrittenForm, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Where each of the text's sentences starts and how long it is, as TTS chunking splits them.</summary>
    private static List<(int Start, int Length)> SentencesOf(string text)
    {
        var sentences = new List<(int, int)>();
        var at = 0;
        foreach (var piece in SentenceSplitter.Split(text))
        {
            var start = text.IndexOf(piece, at, StringComparison.Ordinal);
            if (start < 0)
                return [(0, text.Length)];
            sentences.Add((start, piece.Length));
            at = start + piece.Length;
        }
        return sentences;
    }

    /// <summary>The sentence the match is in, or the run of sentences it spans.</summary>
    private static (int Start, int Length) SentenceAround(
        List<(int Start, int Length)> sentences, PronunciationMatch match, int textLength)
    {
        var first = sentences.LastOrDefault(s => s.Start <= match.Start, (Start: 0, Length: textLength));
        var last = sentences.LastOrDefault(s => s.Start < match.Start + match.Length, first);
        var end = Math.Max(last.Start + last.Length, match.Start + match.Length);
        return (first.Start, end - first.Start);
    }
}
