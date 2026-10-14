using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Stages one new node beside or inside an anchor and reports what it created.
/// <para>
/// The commands endpoint dispatches this with no dialog in front of it, so every request it can
/// see is checked here: a combination that creates no node or one not yet supported, and blank
/// paragraph text, are <see cref="BookMutationRejection.Validation"/>; an anchor the Book does not
/// contain is <see cref="BookMutationRejection.NotFound"/>. Only paragraph-producing combinations
/// are accepted so far.
/// </para>
/// <para>
/// Where the node lands and what it holds at birth stay in
/// <see cref="BookHierarchy.PlanInsertNode"/>, beside the split and merge logic that reasons over
/// the same sibling lists.
/// </para>
/// </summary>
public sealed class InsertNodeMutationImplementation : IBookMutationImplementation<InsertNodeMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        InsertNodeMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var kind = BookHierarchy.InsertedKind(mutation.AnchorKind, mutation.Placement);
        if (kind is null)
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                "Nothing can be inserted against an item this way — use InsertParagraphItem beside it.");
        if (kind != NodeKind.Paragraph)
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                $"Inserting a {kind} ({mutation.Placement} a {mutation.AnchorKind}) is not supported yet.");

        if (string.IsNullOrWhiteSpace(mutation.Text))
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                "A new paragraph needs text — an empty paragraph reads as a pause.");

        var anchorExists = mutation.AnchorKind == NodeKind.Chapter
            ? await db.Chapters.AnyAsync(c => c.Id == mutation.AnchorId, ct)
            : await db.Paragraphs.AnyAsync(p => p.Id == mutation.AnchorId, ct);
        if (!anchorExists)
            throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"No {mutation.AnchorKind.ToString().ToLowerInvariant()} {mutation.AnchorId} to insert against.");

        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var planned = hierarchy.PlanInsertNode(
            mutation.AnchorKind, mutation.AnchorId, mutation.Placement, mutation.Title, mutation.Text);
        if (planned is null)
            return BookMutationEffects.Nothing;

        BookMutationApplier.StageMutation(db, planned);

        var paragraph = (Paragraph)planned.ToAdd[0];
        var item = (ParagraphItem)planned.ToAdd[1];
        return new BookMutationEffects
        {
            // A new Paragraph with an unattributed speech item raises its chapter's audio
            // denominator and attribution-remaining counts — structural, though exact about what
            // it made.
            Scope = BookMutationScope.Exact,
            Facets = BookFacets.Structure,
            CreatedId = paragraph.Id,
            ParagraphIds = [paragraph.Id],
            ParagraphItemIds = [item.Id],
        };
    }
}
