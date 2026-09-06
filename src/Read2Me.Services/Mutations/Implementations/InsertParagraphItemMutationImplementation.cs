using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// The first mutation family to cross <see cref="BookMutations"/> end to end: it stages one new
/// Speech ParagraphItem beside an anchor and reports what it created.
/// <para>
/// The three uncommitted answers are kept apart deliberately, because
/// <c>POST /api/projects/{folder}/commands</c> resolves any command by name and an agent can post
/// this one with no dialog in front of it. Blank text is a <see cref="BookMutationRejection.Validation"/>
/// failure; an anchor the Book does not contain is <see cref="BookMutationRejection.NotFound"/>; a
/// pause anchor is neither — inserting Speech into a pause Paragraph is a structure the readers
/// assume cannot exist, so the request is legal and simply changes nothing.
/// </para>
/// <para>
/// The ordering itself stays in <see cref="Books.BookHierarchy.PlanInsertParagraphItem"/>, beside
/// the merge and split logic that reasons over the same sibling list.
/// </para>
/// </summary>
public sealed class InsertParagraphItemMutationImplementation
    : IBookMutationImplementation<InsertParagraphItemMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        InsertParagraphItemMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mutation.Text))
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                "An inserted item needs text — whitespace alone is not an item.");

        if (!await db.ParagraphItems.AnyAsync(i => i.Id == mutation.AnchorItemId, ct))
            throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"No paragraph item {mutation.AnchorItemId} to insert against.");

        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var planned = hierarchy.PlanInsertParagraphItem(mutation.AnchorItemId, mutation.Position, mutation.Text);
        if (planned is null)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, planned);

        var inserted = (ParagraphItem)planned.ToAdd[0];
        return new BookMutationEffects
        {
            // A new item is a structural change: it raises the chapter's audio denominator and its
            // attribution-remaining count, and can flip the Paragraph into a character Paragraph.
            // The identifiers are exact, so a reader still knows exactly which Paragraph moved.
            Scope = BookMutationScope.Exact,
            Facets = BookFacets.Structure,
            CreatedId = inserted.Id,
            ParagraphIds = [inserted.ParagraphId],
            ParagraphItemIds = [inserted.Id],
        };
    }
}
