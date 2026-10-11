using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Characters;

/// <summary>
/// The one predicate behind Release narration: the narrator-stamped speech items of a scope's
/// narration-only paragraphs, meaning paragraphs with no dialog item at all. The write
/// (<c>ReleaseNarrationMutationImplementation</c>) and the confirm's preview
/// (<c>GetNarrationReleasePreviewAsync</c>) both ask through here, so the count a producer confirms
/// is the count that is written.
/// <para>
/// A speech tag inside a quoted paragraph ("he said") is narration in a paragraph that holds dialog,
/// so it is never releasable. Pauses are nobody's, so they never match either.
/// </para>
/// <para>
/// A Chapter or Part release takes only the paragraphs carrying a <see cref="SpeechMarkers"/> key
/// that repeats in at least <see cref="SpeechMarkers.MinRepetition"/> narration-only paragraphs of
/// their own chapter (ADR 0015), so the real narration around a transcript keeps the narrator. A
/// Paragraph release is unfiltered: picking one paragraph is the producer's explicit intent. The
/// text rules don't translate to SQL, so this loads the scope's narration-only paragraphs and
/// selects in memory.
/// </para>
/// </summary>
public static class NarrationRelease
{
    /// <summary>
    /// The wire binds enums from integers too, so a scope the enum does not name can arrive; callers
    /// refuse it first with this, and <see cref="SelectAsync"/> treats it as a bug.
    /// </summary>
    public static bool IsKnownScope(NarrationReleaseScope scope) => Enum.IsDefined(scope);

    public static async Task<NarrationReleaseSelection> SelectAsync(
        ProjectDbContext db, NarrationReleaseScope scope, Guid nodeId, CancellationToken ct = default)
    {
        var inScope = scope switch
        {
            NarrationReleaseScope.Part      => db.ParagraphItems.Where(i => i.Paragraph.Chapter.PartId == nodeId),
            NarrationReleaseScope.Chapter   => db.ParagraphItems.Where(i => i.Paragraph.ChapterId == nodeId),
            NarrationReleaseScope.Paragraph => db.ParagraphItems.Where(i => i.ParagraphId == nodeId),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown release scope."),
        };

        var paragraphsWithDialog = db.ParagraphItems
            .Where(NarrationRule.IsDialogExpression)
            .Select(i => i.ParagraphId);

        var rows = await inScope
            .Where(ParagraphItemKinds.IsSpeechExpression)
            .Where(NarrationRule.IsNarrationExpression)
            .Where(i => !paragraphsWithDialog.Contains(i.ParagraphId))
            .Select(i => new
            {
                i.Id,
                i.ParagraphId,
                i.Paragraph.ChapterId,
                i.Order,
                i.Text,
                HasAudio = i.AudioFileName != null,
            })
            .ToListAsync(ct);

        var paragraphs = rows
            .GroupBy(r => (r.ParagraphId, r.ChapterId))
            .Select(g =>
            {
                var items = g.OrderBy(r => r.Order, StringComparer.Ordinal).ToList();
                return new ReleasableParagraph(
                    g.Key.ParagraphId,
                    g.Key.ChapterId,
                    string.Join(' ', items.Select(r => r.Text ?? string.Empty)),
                    [.. items.Select(r => new ReleasableItem(r.Id, r.HasAudio))]);
            })
            .ToList();

        if (scope == NarrationReleaseScope.Paragraph)
            return new NarrationReleaseSelection(paragraphs, []);

        var roster = SpeechMarkers.Roster(
            await db.Characters.Select(c => c.Name)
                .Concat(db.CharacterAliases.Select(a => a.Name))
                .ToListAsync(ct));

        // Each chapter is judged by its own markers: one transcript chapter must not carry look-alike
        // lines elsewhere in a Part over the gate.
        var released = paragraphs
            .Select(p => (Paragraph: p, Key: SpeechMarkers.KeyOf(p.Text, roster)))
            .Where(k => k.Key is not null)
            .GroupBy(k => (k.Paragraph.ChapterId, k.Key))
            .Where(g => g.Count() >= SpeechMarkers.MinRepetition)
            .SelectMany(g => g)
            .ToList();

        var markers = released
            .GroupBy(k => k.Key!)
            .Select(g => new SpeechMarkerCount(g.Key, g.Count()))
            .OrderByDescending(m => m.Paragraphs)
            .ThenBy(m => m.Marker, StringComparer.Ordinal)
            .ToList();

        return new NarrationReleaseSelection([.. released.Select(k => k.Paragraph)], markers);
    }
}

/// <summary>
/// What a Release narration takes: the paragraphs it releases, and for a Chapter or Part the speech
/// markers that passed the gate with how many released paragraphs each covers, summed across the
/// scope's chapters (ordered by count, then marker). Empty markers for a Paragraph release.
/// </summary>
public sealed record NarrationReleaseSelection(
    IReadOnlyList<ReleasableParagraph> Paragraphs, IReadOnlyList<SpeechMarkerCount> Markers)
{
    public IEnumerable<ReleasableItem> Items => Paragraphs.SelectMany(p => p.Items);
}

/// <summary>A narration-only paragraph: its speech items' texts in item order, joined by one space.</summary>
public sealed record ReleasableParagraph(
    Guid ParagraphId, Guid ChapterId, string Text, IReadOnlyList<ReleasableItem> Items);

public sealed record ReleasableItem(Guid Id, bool HasAudio);

public sealed record SpeechMarkerCount(string Marker, int Paragraphs);
