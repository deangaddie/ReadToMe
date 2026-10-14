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
/// see is checked here: a combination that creates no node (any speech-item anchor), and blank
/// paragraph or item text, and an item at a pause paragraph's start or end, are
/// <see cref="BookMutationRejection.Validation"/>; an anchor the Book does not contain is
/// <see cref="BookMutationRejection.NotFound"/>. A new Volume, Part, Chapter or Paragraph is
/// accepted, and so is a speech item at a paragraph's start or end.
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
        if (kind is NodeKind.Paragraph or NodeKind.ParagraphItem && string.IsNullOrWhiteSpace(mutation.Text))
            throw new BookMutationRejectedException(
                BookMutationRejection.Validation,
                kind == NodeKind.Paragraph
                    ? "A new paragraph needs text — an empty paragraph reads as a pause."
                    : "An inserted item needs text — whitespace alone is not an item.");

        var anchorExists = mutation.AnchorKind switch
        {
            NodeKind.Volume => await db.Volumes.AnyAsync(v => v.Id == mutation.AnchorId, ct),
            NodeKind.Part => await db.Parts.AnyAsync(p => p.Id == mutation.AnchorId, ct),
            NodeKind.Chapter => await db.Chapters.AnyAsync(c => c.Id == mutation.AnchorId, ct),
            _ => await db.Paragraphs.AnyAsync(p => p.Id == mutation.AnchorId, ct),
        };
        if (!anchorExists)
            throw new BookMutationRejectedException(
                BookMutationRejection.NotFound,
                $"No {mutation.AnchorKind.ToString().ToLowerInvariant()} {mutation.AnchorId} to insert against.");

        var hierarchy = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var planned = hierarchy.PlanInsertNode(
            mutation.AnchorKind, mutation.AnchorId, mutation.Placement, mutation.Title, mutation.Text);
        if (planned is null)
        {
            // With text and a known paragraph, the only item the planner turns down is one at a
            // pause paragraph's start or end: never valid there, so refused rather than a no-op.
            if (kind == NodeKind.ParagraphItem)
                throw new BookMutationRejectedException(
                    BookMutationRejection.Validation,
                    "A pause paragraph holds no speech — insert a paragraph before or after it.");
            return BookMutationEffects.Nothing;
        }

        BookMutationApplier.StageMutation(db, planned);

        if (planned.ToAdd[0] is ParagraphItem inserted)
        {
            // A speech item first or last in its paragraph: the same receipt as an item insert.
            return new BookMutationEffects
            {
                Scope = BookMutationScope.Exact,
                Facets = BookFacets.Structure,
                CreatedId = inserted.Id,
                ParagraphIds = [inserted.ParagraphId],
                ParagraphItemIds = [inserted.Id],
            };
        }

        if (planned.ToAdd[0] is not Paragraph paragraph)
        {
            // An empty container: no paragraph or item, so nothing to attribute, but the tree a
            // reader holds gains a node.
            var nodeId = planned.ToAdd[0] switch
            {
                Volume v => v.Id,
                Part p => p.Id,
                Chapter c => c.Id,
                var other => throw new InvalidOperationException($"Unexpected {other.GetType().Name} planned."),
            };
            return new BookMutationEffects
            {
                Scope = BookMutationScope.Exact,
                Facets = BookFacets.Structure,
                CreatedId = nodeId,
                NodeIds = [nodeId],
            };
        }

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
