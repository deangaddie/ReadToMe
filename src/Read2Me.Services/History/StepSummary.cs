using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services.History;

/// <summary>
/// A step's "what changed", in user terms, from what its commits changed: how many paragraphs, and
/// where — "4 paragraphs in Chapter 3", "12 paragraphs across 3 chapters", "The Storm". Null when
/// the receipts name nothing in the Book to count or place, as a roster or Voice change does: the
/// step's label says it all then.
/// <para>
/// Read inside the commit's transaction, so it describes the Book as the step leaves it. Ids the
/// Book no longer holds — what a delete removed — drop out rather than fail.
/// </para>
/// </summary>
internal static class StepSummary
{
    private enum Level { Volume, Part, Chapter }

    private sealed record Place(Level Level, string? Title, int Position)
    {
        /// <summary>The node's title, or its Placeholder title: "Untitled chapter 3", numbered among its siblings.</summary>
        public string Name => string.IsNullOrWhiteSpace(Title)
            ? $"Untitled {Level.ToString().ToLowerInvariant()} {Position}"
            : Title;
    }

    public static async Task<string?> DescribeAsync(ProjectDbContext db, StepChanges changes, CancellationToken ct)
    {
        var itemParagraphs = changes.ParagraphItemIds.Count == 0
            ? []
            : await db.ParagraphItems.Where(i => changes.ParagraphItemIds.Contains(i.Id)).Select(i => i.ParagraphId).ToListAsync(ct);
        var paragraphIds = changes.ParagraphIds.Concat(itemParagraphs).ToHashSet();

        var chapterIds = paragraphIds.Count == 0
            ? []
            : await db.Paragraphs.Where(p => paragraphIds.Contains(p.Id)).Select(p => p.ChapterId).Distinct().ToListAsync(ct);
        var paragraphs = paragraphIds.Count == 0
            ? 0
            : await db.Paragraphs.CountAsync(p => paragraphIds.Contains(p.Id), ct);

        var places = await PlacesAsync(db, changes.NodeIds.Concat(chapterIds).ToHashSet(), ct);

        var count = paragraphs switch
        {
            0 => null,
            1 => "1 paragraph",
            _ => $"{paragraphs} paragraphs",
        };
        var place = places.Count switch
        {
            0 => null,
            1 => places[0].Name,
            _ when places.All(p => p.Level == places[0].Level) => $"{places.Count} {places[0].Level.ToString().ToLowerInvariant()}s",
            _ => $"{places.Count} places",
        };

        return (count, place) switch
        {
            (null, null) => null,
            (null, _) => place,
            (_, null) => count,
            _ when places.Count == 1 => $"{count} in {place}",
            _ => $"{count} across {place}",
        };
    }

    /// <summary>The Volumes, Parts and Chapters among <paramref name="nodeIds"/>, each with its position among its siblings.</summary>
    private static async Task<List<Place>> PlacesAsync(ProjectDbContext db, HashSet<Guid> nodeIds, CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return [];

        var places = new List<Place>();
        places.AddRange(await db.Chapters.Where(c => nodeIds.Contains(c.Id))
            .Select(c => new Place(Level.Chapter, c.Title,
                db.Chapters.Count(s => s.PartId == c.PartId && string.Compare(s.Order, c.Order) <= 0)))
            .ToListAsync(ct));
        places.AddRange(await db.Parts.Where(p => nodeIds.Contains(p.Id))
            .Select(p => new Place(Level.Part, p.Title,
                db.Parts.Count(s => s.VolumeId == p.VolumeId && string.Compare(s.Order, p.Order) <= 0)))
            .ToListAsync(ct));
        places.AddRange(await db.Volumes.Where(v => nodeIds.Contains(v.Id))
            .Select(v => new Place(Level.Volume, v.Title,
                db.Volumes.Count(s => string.Compare(s.Order, v.Order) <= 0)))
            .ToListAsync(ct));
        return places;
    }
}
