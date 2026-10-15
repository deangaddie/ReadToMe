using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>What a Node or Item insertion changed, read from the node its plan created.</summary>
internal static class InsertionEffects
{
    public static BookMutationEffects Of(HierarchyPlan.Planned planned)
    {
        var created = planned.Created
            ?? throw new InvalidOperationException("An insertion plan always names the node it creates.");
        return created.Kind switch
        {
            // A new item is a structural change: it raises the chapter's audio denominator and its
            // attribution-remaining count, and can flip the Paragraph into a character Paragraph.
            // The identifiers are exact, so a reader still knows exactly which Paragraph moved.
            NodeKind.ParagraphItem => new BookMutationEffects
            {
                Scope = BookMutationScope.Exact,
                Facets = BookFacets.Structure,
                CreatedId = created.Id,
                CreatedParentId = created.ParentId,
                ParagraphIds = [created.ParentId!.Value],
                ParagraphItemIds = [created.Id],
            },
            // A new Paragraph with an unattributed speech item raises its chapter's audio
            // denominator and attribution-remaining counts — structural, though exact about what
            // it made.
            NodeKind.Paragraph => new BookMutationEffects
            {
                Scope = BookMutationScope.Exact,
                Facets = BookFacets.Structure,
                CreatedId = created.Id,
                CreatedParentId = created.ParentId,
                ParagraphIds = [created.Id],
                ParagraphItemIds = [.. planned.Mutation.ToAdd.OfType<ParagraphItem>().Select(i => i.Id)],
            },
            // An empty container: no paragraph or item, so nothing to attribute, but the tree a
            // reader holds gains a node.
            _ => new BookMutationEffects
            {
                Scope = BookMutationScope.Exact,
                Facets = BookFacets.Structure,
                CreatedId = created.Id,
                CreatedParentId = created.ParentId,
                NodeIds = [created.Id],
            },
        };
    }
}
