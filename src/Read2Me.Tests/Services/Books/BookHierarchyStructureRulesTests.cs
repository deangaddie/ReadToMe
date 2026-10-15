using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    /// <summary>
    /// The Structure rules — one entry per anchor kind — and the Book Hierarchy that plans from them.
    /// The table is asserted against the rules as written in the glossary, and every anchor kind ×
    /// placement is planned once: an allowed placement creates the kind the table names, any other
    /// is Refused(Validation), so the menu that renders from the table never offers a refused insert.
    /// </summary>
    public class BookHierarchyStructureRulesTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        private sealed record Book(BookHierarchy H, Dictionary<StructureAnchor, Guid> Anchors);

        /// <summary>One of every anchor kind: a volume, part and chapter holding a speech paragraph and a pause paragraph.</summary>
        private static Book MakeBook()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var chapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var speech = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = Key() };
            var pause = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = Key(speech.Order) };
            var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = speech.Id, Order = Key(), ItemType = ParagraphItemType.Speech, Text = "Said." };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [chapter] },
                Paragraphs = { [chapter.Id] = [speech, pause] },
                Items =
                {
                    [speech.Id] = [item],
                    [pause.Id] = [new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pause.Id, Order = Key(), ItemType = ParagraphItemType.ParagraphPause }],
                },
            };
            return new Book(h, new()
            {
                [StructureAnchor.Volume] = vol.Id,
                [StructureAnchor.Part] = part.Id,
                [StructureAnchor.Chapter] = chapter.Id,
                [StructureAnchor.Paragraph] = speech.Id,
                [StructureAnchor.PauseParagraph] = pause.Id,
                [StructureAnchor.ParagraphItem] = item.Id,
            });
        }

        /// <summary>
        /// The insert the planner is asked for: InsertNode for every anchor but an item, which is
        /// InsertParagraphItem's (InsertNode refuses any item anchor and names that route).
        /// </summary>
        private static HierarchyPlan Plan(Book b, StructureRule rule, Placement placement, string? text)
        {
            var id = b.Anchors[rule.Anchor];
            if (rule.Anchor != StructureAnchor.ParagraphItem)
                return b.H.PlanInsertNode(rule.NodeKind, id, placement, title: null, text);
            return placement switch
            {
                Placement.Before => b.H.PlanInsertParagraphItem(id, InsertPosition.Before, text),
                Placement.After => b.H.PlanInsertParagraphItem(id, InsertPosition.After, text),
                _ => b.H.PlanInsertNode(NodeKind.ParagraphItem, id, placement, title: null, text),
            };
        }

        [Fact]
        public void TheTable_HoldsOneEntryPerAnchorKind_AsTheGlossaryWritesThem()
        {
            static string Row(StructureRule r) =>
                $"{r.Anchor}({r.NodeKind}, min {r.MinimumCount}): "
                + string.Join(", ", r.Placements.Select(p => $"{p.Placement}→{p.Creates}{(p.TextRequired ? "+text" : "")}"));

            Assert.Equal(
                [
                    "Volume(Volume, min 1): Before→Volume, After→Volume, AtStart→Part, AtEnd→Part",
                    "Part(Part, min 0): Before→Part, After→Part, AtStart→Chapter, AtEnd→Chapter",
                    "Chapter(Chapter, min 0): Before→Chapter, After→Chapter, AtStart→Paragraph+text, AtEnd→Paragraph+text",
                    "Paragraph(Paragraph, min 0): Before→Paragraph+text, After→Paragraph+text, AtStart→ParagraphItem+text, AtEnd→ParagraphItem+text",
                    "PauseParagraph(Paragraph, min 0): Before→Paragraph+text, After→Paragraph+text",
                    "ParagraphItem(ParagraphItem, min 0): Before→ParagraphItem+text, After→ParagraphItem+text",
                ],
                StructureRules.All.Select(Row));
        }

        [Fact]
        public void EachAllowedPlacement_PlansTheKindTheTableNames()
        {
            foreach (var rule in StructureRules.All)
            {
                foreach (var allowed in rule.Placements)
                {
                    var plan = Plan(MakeBook(), rule, allowed.Placement, "New.");

                    var planned = Assert.IsType<HierarchyPlan.Planned>(plan);
                    Assert.Equal(allowed.Creates, planned.Created!.Kind);
                }
            }
        }

        [Fact]
        public void EachOtherPlacement_IsRefusedAsValidation()
        {
            foreach (var rule in StructureRules.All)
            {
                foreach (var placement in Enum.GetValues<Placement>().Where(p => rule.Allows(p) is null))
                {
                    var refused = Assert.IsType<HierarchyPlan.Refused>(Plan(MakeBook(), rule, placement, "New."));
                    Assert.Equal(BookMutationRejection.Validation, refused.Rejection);
                }
            }
        }

        [Fact]
        public void APlacementThatNeedsText_IsRefusedWithoutIt_AndOneThatDoesNot_PlansWithout()
        {
            foreach (var rule in StructureRules.All)
            {
                foreach (var allowed in rule.Placements)
                {
                    var plan = Plan(MakeBook(), rule, allowed.Placement, "  ");

                    if (allowed.TextRequired)
                        Assert.Equal(BookMutationRejection.Validation, Assert.IsType<HierarchyPlan.Refused>(plan).Rejection);
                    else
                        Assert.IsType<HierarchyPlan.Planned>(plan);
                }
            }
        }

        [Fact]
        public void TheVolumeDeletePlanner_StopsAtTheTablesMinimumCount()
        {
            var b = MakeBook();
            var minimum = StructureRules.For(StructureAnchor.Volume).MinimumCount;
            Assert.Equal(minimum, b.H.Volumes.Count);

            Assert.IsType<HierarchyPlan.Refused>(b.H.PlanDeleteVolume(b.Anchors[StructureAnchor.Volume]));
        }
    }
}
