using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the destructive structural family shares — the five merges, the five deletions, and
/// clearing the Book's whole content (ADR 0007).
/// <para>
/// Removal is the case a Book View cannot recover from by recounting: a node the reader has open,
/// or a row they have selected, can simply stop existing. So these report two things carefully —
/// the merge relationship, which is how expansion follows the survivor, and an honest scope, which
/// is how a reader knows when the identifiers it was given are not the whole story.
/// </para>
/// </summary>
internal static class DestructiveEffects
{
    /// <summary>
    /// What removing content changes. Deleting a subtree takes its items' text, speakers, audio and
    /// reviews with it, so every item-scoped facet moved even though only structure was asked for.
    /// <para>
    /// Deliberately coarse: a Volume that held no items reports the same set. Over-reporting a facet
    /// costs a reader an extra read, while under-reporting it leaves them rendering data the Book no
    /// longer has.
    /// </para>
    /// </summary>
    public const BookFacets RemovedFacets =
        BookFacets.Structure | BookFacets.ItemText | BookFacets.Attribution
        | BookFacets.Audio | BookFacets.Reviews;

    /// <summary>A removal that cannot name what went with it — the safe default for this family.</summary>
    public static BookMutationEffects Removed { get; } = new()
    {
        Scope = BookMutationScope.WholeProject,
        Facets = RemovedFacets,
    };

    /// <summary>
    /// A merge of two hierarchy nodes: the survivor adopts the deleted node's children, and neither
    /// the moved children nor anything under them is named — so the scope is honestly whole-project
    /// however exact the relationship between the two nodes is.
    /// </summary>
    public static BookMutationEffects Folded(BookMergePlan plan) => new()
    {
        Scope = BookMutationScope.WholeProject,
        Facets = BookFacets.Structure,
        Structural =
            [new BookStructuralRelation(BookStructuralRelationKind.Merge, plan.DeletedId, plan.SurvivorId)],
    };

    /// <summary>The node named by a destructive mutation that the Book does not contain.</summary>
    public static BookMutationRejectedException NotFound(string what, Guid id) =>
        new(BookMutationRejection.NotFound, $"No {what} {id} to remove.");
}

// ── merges ───────────────────────────────────────────────────────────────────
// Each of these has the same shape: confirm the node exists, plan, stage. The distinction the
// legacy handlers could not draw is between the two ways a merge does nothing — a node the Book
// does not have is NotFound, while the first or last sibling having nothing to merge into is a
// legal gesture that changes nothing.

public sealed class MergeVolumeMutationImplementation : IBookMutationImplementation<MergeVolumeMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MergeVolumeMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (!hierarchy.Volumes.Any(v => v.Id == mutation.VolumeId))
            throw DestructiveEffects.NotFound("volume", mutation.VolumeId);

        if (hierarchy.PlanMergeVolume(mutation.VolumeId, mutation.Direction) is not { } plan)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, plan.Mutation);
        return DestructiveEffects.Folded(plan);
    }
}

public sealed class MergePartMutationImplementation : IBookMutationImplementation<MergePartMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MergePartMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Parts, mutation.PartId, p => p.Id) is null)
            throw DestructiveEffects.NotFound("part", mutation.PartId);

        if (hierarchy.PlanMergePart(mutation.PartId, mutation.Direction) is not { } plan)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, plan.Mutation);
        return DestructiveEffects.Folded(plan);
    }
}

public sealed class MergeChapterMutationImplementation : IBookMutationImplementation<MergeChapterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MergeChapterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Chapters, mutation.ChapterId, c => c.Id) is null)
            throw DestructiveEffects.NotFound("chapter", mutation.ChapterId);

        if (hierarchy.PlanMergeChapter(mutation.ChapterId, mutation.Direction) is not { } plan)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, plan.Mutation);
        return DestructiveEffects.Folded(plan);
    }
}

public sealed class MergeParagraphMutationImplementation : IBookMutationImplementation<MergeParagraphMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MergeParagraphMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Paragraphs, mutation.ParagraphId, p => p.Id) is null)
            throw DestructiveEffects.NotFound("paragraph", mutation.ParagraphId);

        if (hierarchy.PlanMergeParagraph(mutation.ParagraphId, mutation.Direction) is not { } plan)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, plan.Mutation);

        // Exhaustive, like the paragraph split: two Paragraphs and the items that moved between
        // them are everything this touched. The Chapter's roll-up denominators move too, but they
        // are derived rather than touched, and BookFacets.Structure already says to rebuild.
        //
        // Facets are the removal set rather than Structure alone. No item's own data changed, but
        // the survivor now holds items carrying speakers, audio and reviews it did not have, and a
        // reader told "structure only" about that Paragraph would keep item-derived state that no
        // longer describes it.
        return DestructiveEffects.Folded(plan) with
        {
            Scope = BookMutationScope.Exact,
            Facets = DestructiveEffects.RemovedFacets,
            ParagraphIds = [plan.SurvivorId, plan.DeletedId],
            ParagraphItemIds = [.. plan.Mutation.ToUpdate.OfType<ParagraphItem>().Select(i => i.Id)],
        };
    }
}

public sealed class MergeParagraphItemMutationImplementation
    : IBookMutationImplementation<MergeParagraphItemMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        MergeParagraphItemMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        if (HierarchyLookup.OwnerOf(hierarchy.Items, mutation.ItemId, i => i.Id) is not { } paragraphId)
            throw DestructiveEffects.NotFound("paragraph item", mutation.ItemId);

        if (hierarchy.PlanMergeParagraphItem(mutation.ItemId, mutation.Direction) is not { } plan)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, plan.Mutation);

        // The survivor's text grew, and the other item is gone with its speaker, audio and review —
        // all of it inside one Paragraph, which is the whole of what changed.
        return DestructiveEffects.Folded(plan) with
        {
            Scope = BookMutationScope.Exact,
            Facets = DestructiveEffects.RemovedFacets,
            ParagraphIds = [paragraphId],
            ParagraphItemIds = [plan.SurvivorId, plan.DeletedId],
        };
    }
}

// ── deletions ────────────────────────────────────────────────────────────────
// A deletion cascades in the database, so what goes below the named node is never staged here. The
// two Paragraph-level deletions can still be exact, because their subtree is one read; the three
// hierarchy levels above them say whole-project rather than guess.

public sealed class DeleteVolumeMutationImplementation : IBookMutationImplementation<DeleteVolumeMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteVolumeMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var volume = await db.Volumes.FirstOrDefaultAsync(v => v.Id == mutation.VolumeId, ct)
            ?? throw DestructiveEffects.NotFound("volume", mutation.VolumeId);

        db.Volumes.Remove(volume);
        return DestructiveEffects.Removed;
    }
}

public sealed class DeletePartMutationImplementation : IBookMutationImplementation<DeletePartMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeletePartMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == mutation.PartId, ct)
            ?? throw DestructiveEffects.NotFound("part", mutation.PartId);

        db.Parts.Remove(part);
        return DestructiveEffects.Removed;
    }
}

public sealed class DeleteChapterMutationImplementation : IBookMutationImplementation<DeleteChapterMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteChapterMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == mutation.ChapterId, ct)
            ?? throw DestructiveEffects.NotFound("chapter", mutation.ChapterId);

        db.Chapters.Remove(chapter);
        return DestructiveEffects.Removed;
    }
}

public sealed class DeleteParagraphMutationImplementation : IBookMutationImplementation<DeleteParagraphMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteParagraphMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var paragraph = await db.Paragraphs.FirstOrDefaultAsync(p => p.Id == mutation.ParagraphId, ct)
            ?? throw DestructiveEffects.NotFound("paragraph", mutation.ParagraphId);

        // Read before the delete: afterwards there is nothing left to ask which items went with it.
        var itemIds = await db.ParagraphItems
            .Where(i => i.ParagraphId == paragraph.Id)
            .Select(i => i.Id)
            .ToListAsync(ct);

        db.Paragraphs.Remove(paragraph);
        return DestructiveEffects.Removed with
        {
            Scope = BookMutationScope.Exact,
            ParagraphIds = [paragraph.Id],
            ParagraphItemIds = itemIds,
        };
    }
}

public sealed class DeleteParagraphItemMutationImplementation
    : IBookMutationImplementation<DeleteParagraphItemMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        DeleteParagraphItemMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await db.ParagraphItems.FirstOrDefaultAsync(i => i.Id == mutation.ItemId, ct)
            ?? throw DestructiveEffects.NotFound("paragraph item", mutation.ItemId);

        db.ParagraphItems.Remove(item);
        return DestructiveEffects.Removed with
        {
            Scope = BookMutationScope.Exact,
            ParagraphIds = [item.ParagraphId],
            ParagraphItemIds = [item.Id],
        };
    }
}

/// <summary>
/// Empties the Book. Nothing that survives can be named and a reader holding expansion or a
/// selection over any of it has to let all of it go, so this degrades to the safest scope there is.
/// <para>
/// Clearing an already-empty Book changes nothing: the reread that runs this before rebuilding
/// should not consume a revision, publish a receipt, or make every open Book View rebuild for a
/// Book that has not moved.
/// </para>
/// </summary>
public sealed class ClearBookContentMutationImplementation
    : IBookMutationImplementation<ClearBookContentMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        ClearBookContentMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var removed = await BookContentRemoval.ClearAsync(db, ct);
        return removed == 0 ? BookMutationEffects.Nothing : DestructiveEffects.Removed;
    }
}

/// <summary>
/// Emptying a Book of its content, shared by the mutation that only does that and the reread that
/// does it as the first half of a replacement (ADR 0007). A reread cannot commit the clear and then
/// commit the import, because that would publish the empty Book in between — so it borrows the
/// removal rather than the mutation.
/// </summary>
internal static class BookContentRemoval
{
    /// <summary>Removes every node and item, and returns how many rows went.</summary>
    public static async Task<int> ClearAsync(ProjectDbContext db, CancellationToken ct)
    {
        // Bottom-up, and as set operations rather than through the change tracker: a Book's content
        // is far too large to load merely in order to delete it.
        var removed = await db.ParagraphItems.ExecuteDeleteAsync(ct);
        removed += await db.Paragraphs.ExecuteDeleteAsync(ct);
        removed += await db.Chapters.ExecuteDeleteAsync(ct);
        removed += await db.Parts.ExecuteDeleteAsync(ct);
        removed += await db.Volumes.ExecuteDeleteAsync(ct);
        return removed;
    }
}
