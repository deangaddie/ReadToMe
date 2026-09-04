using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What every split shares. A split creates one node and moves the source's later children into
/// it, so the moved children are never named: the scope is honestly whole-project even though the
/// relationship between the two nodes is exact.
/// <para>
/// That relationship is the point. It is what lets a reader keep the reader's place — open the new
/// sibling if the source was open — without the writer knowing anything about expansion.
/// </para>
/// </summary>
internal static class SplitEffects
{
    public static BookMutationEffects Moving(Guid sourceId, Guid createdId) => new()
    {
        Scope = BookMutationScope.WholeProject,
        Facets = BookFacets.Structure,
        CreatedId = createdId,
        Structural = [new BookStructuralRelation(BookStructuralRelationKind.Split, sourceId, createdId)],
    };

    /// <summary>The node named by a split that the Book does not contain.</summary>
    public static BookMutationRejectedException NotFound(string what, Guid id) =>
        new(BookMutationRejection.NotFound, $"No {what} {id} to split at.");
}

public sealed class SplitAtPartMutationImplementation : IBookMutationImplementation<SplitAtPartMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SplitAtPartMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Parts, mutation.PartId, p => p.Id) is not { } source)
            throw SplitEffects.NotFound("part", mutation.PartId);

        // The part exists, so a planner that declines has found nothing to move: a legal gesture
        // that changes nothing, which is not the same answer as naming a part the Book lacks.
        var planned = hierarchy.PlanSplitVolume(mutation.PartId, mutation.NewVolumeTitle);
        if (planned is null) return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, planned);
        return SplitEffects.Moving(source, ((Volume)planned.ToAdd[0]).Id);
    }
}

public sealed class SplitAtChapterMutationImplementation : IBookMutationImplementation<SplitAtChapterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SplitAtChapterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Chapters, mutation.ChapterId, c => c.Id) is not { } source)
            throw SplitEffects.NotFound("chapter", mutation.ChapterId);

        var planned = hierarchy.PlanSplitPart(mutation.ChapterId, mutation.NewPartTitle);
        if (planned is null) return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, planned);
        return SplitEffects.Moving(source, ((Part)planned.ToAdd[0]).Id);
    }
}

public sealed class SplitAtParagraphMutationImplementation : IBookMutationImplementation<SplitAtParagraphMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SplitAtParagraphMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Paragraphs, mutation.ParagraphId, p => p.Id) is not { } source)
            throw SplitEffects.NotFound("paragraph", mutation.ParagraphId);

        var planned = hierarchy.PlanSplitChapter(mutation.ParagraphId, mutation.NewChapterTitle);
        if (planned is null) return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, planned);
        return SplitEffects.Moving(source, ((Chapter)planned.ToAdd[0]).Id);
    }
}

public sealed class SplitAtItemMutationImplementation : IBookMutationImplementation<SplitAtItemMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SplitAtItemMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Items, mutation.ItemId, i => i.Id) is not { } source)
            throw SplitEffects.NotFound("item", mutation.ItemId);

        var planned = hierarchy.PlanSplitParagraph(mutation.ItemId);
        if (planned is null) return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, planned);
        var created = (Paragraph)planned.ToAdd[0];

        // The one split whose effects are exhaustive: two Paragraphs and the items that moved
        // between them are everything it touched. The Chapter's roll-up denominators move too, but
        // they are derived rather than touched — and BookFacets.Structure already tells a reader to
        // rebuild for exactly that reason.
        return SplitEffects.Moving(source, created.Id) with
        {
            Scope = BookMutationScope.Exact,
            ParagraphIds = [source, created.Id],
            ParagraphItemIds = [.. planned.ToUpdate.OfType<ParagraphItem>().Select(i => i.Id)],
        };
    }
}
