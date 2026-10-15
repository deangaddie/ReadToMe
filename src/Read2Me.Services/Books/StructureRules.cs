using Read2Me.Core.Models;

namespace Read2Me.Services.Books
{
    /// <summary>
    /// What a Structure rules entry is keyed by. A pause paragraph is a Paragraph to the commands
    /// but its own anchor here: it takes no speech item at its start or end.
    /// </summary>
    public enum StructureAnchor { Volume, Part, Chapter, Paragraph, PauseParagraph, ParagraphItem }

    /// <summary>One placement an anchor allows: the kind it creates, and whether that node needs text.</summary>
    public sealed record StructurePlacement(Placement Placement, NodeKind Creates, bool TextRequired);

    /// <summary>
    /// One Structure rules entry. <see cref="NodeKind"/> is the kind a command names the anchor
    /// as; <see cref="MinimumCount"/> is how many of that kind the book always keeps.
    /// </summary>
    public sealed record StructureRule(
        StructureAnchor Anchor, NodeKind NodeKind, IReadOnlyList<StructurePlacement> Placements, int MinimumCount)
    {
        /// <summary>The placement as this anchor allows it, or null when it does not.</summary>
        public StructurePlacement? Allows(Placement placement) =>
            Placements.FirstOrDefault(p => p.Placement == placement);
    }

    /// <summary>
    /// The Structure rules (context/book-structure.md): what may be inserted where, what each
    /// insert creates, whether it needs text, and the minimum count kept of each kind. The
    /// <see cref="BookHierarchy"/> plans from this table, and the book overview sends it to the web
    /// app, whose node menu renders from it — no rule is written anywhere else.
    ///
    /// Before/After make a sibling at the anchor's own level, AtStart/AtEnd a child one level down.
    /// A new Paragraph or Speech item needs text: an empty paragraph reads as a pause, and
    /// whitespace alone is not an item. A book keeps one Volume, so there is always a node to
    /// insert from.
    /// </summary>
    public static class StructureRules
    {
        public static IReadOnlyList<StructureRule> All { get; } =
        [
            Rule(StructureAnchor.Volume, NodeKind.Volume, minimumCount: 1, beside: NodeKind.Volume, inside: NodeKind.Part),
            Rule(StructureAnchor.Part, NodeKind.Part, minimumCount: 0, beside: NodeKind.Part, inside: NodeKind.Chapter),
            Rule(StructureAnchor.Chapter, NodeKind.Chapter, minimumCount: 0, beside: NodeKind.Chapter, inside: NodeKind.Paragraph),
            Rule(StructureAnchor.Paragraph, NodeKind.Paragraph, minimumCount: 0, beside: NodeKind.Paragraph, inside: NodeKind.ParagraphItem),
            Rule(StructureAnchor.PauseParagraph, NodeKind.Paragraph, minimumCount: 0, beside: NodeKind.Paragraph, inside: null),
            Rule(StructureAnchor.ParagraphItem, NodeKind.ParagraphItem, minimumCount: 0, beside: NodeKind.ParagraphItem, inside: null),
        ];

        public static StructureRule For(StructureAnchor anchor) => All.First(r => r.Anchor == anchor);

        private static StructureRule Rule(
            StructureAnchor anchor, NodeKind nodeKind, int minimumCount, NodeKind beside, NodeKind? inside)
        {
            List<StructurePlacement> placements =
            [
                Place(Placement.Before, beside),
                Place(Placement.After, beside),
            ];
            if (inside is { } child)
                placements.AddRange([Place(Placement.AtStart, child), Place(Placement.AtEnd, child)]);
            return new StructureRule(anchor, nodeKind, placements, minimumCount);
        }

        private static StructurePlacement Place(Placement placement, NodeKind creates) =>
            new(placement, creates, TextRequired: creates is NodeKind.Paragraph or NodeKind.ParagraphItem);
    }
}
