using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    /// <summary>
    /// PlanInsertNode — which node an insert creates, under which parent, where it falls among its
    /// new siblings and what it holds at birth, decided entirely in memory. An insert only ever adds:
    /// no existing node is reordered, reparented or removed.
    /// </summary>
    public class BookHierarchyInsertNodeTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        private static bool Before(string a, string b) => string.Compare(a, b, StringComparison.Ordinal) < 0;

        private sealed record Book(
            BookHierarchy H, Chapter Full, Paragraph First, Paragraph Pause, Paragraph Last, Chapter Empty);

        /// <summary>
        /// Two chapters in one part: the first holds a speech paragraph, a pause paragraph and another
        /// speech paragraph; the second is empty, as a split at its first paragraph leaves it.
        /// </summary>
        private static Book MakeBook()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var full = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var empty = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key(full.Order) };

            var first = new Paragraph { Id = Guid.NewGuid(), ChapterId = full.Id, Order = Key() };
            var pause = new Paragraph { Id = Guid.NewGuid(), ChapterId = full.Id, Order = Key(first.Order) };
            var last = new Paragraph { Id = Guid.NewGuid(), ChapterId = full.Id, Order = Key(pause.Order) };

            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [full, empty] },
                Paragraphs = { [full.Id] = [first, pause, last] },
                Items =
                {
                    [first.Id] = [Speech(first, "First.")],
                    [pause.Id] = [new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pause.Id, Order = Key(), ItemType = ParagraphItemType.ParagraphPause }],
                    [last.Id] = [Speech(last, "Last.")],
                },
            };
            return new Book(h, full, first, pause, last, empty);
        }

        private static ParagraphItem Speech(Paragraph p, string text) =>
            new() { Id = Guid.NewGuid(), ParagraphId = p.Id, Order = Key(), ItemType = ParagraphItemType.Speech, Text = text };

        /// <summary>The plan's new Paragraph, after asserting the plan adds it and its one item and nothing else.</summary>
        private static Paragraph AddedParagraph(HierarchyMutation? mutation)
        {
            Assert.NotNull(mutation);
            Assert.Empty(mutation!.ToDelete);
            Assert.Empty(mutation.ToUpdate);
            Assert.Equal(2, mutation.ToAdd.Count);
            var paragraph = Assert.IsType<Paragraph>(mutation.ToAdd[0]);
            var item = Assert.IsType<ParagraphItem>(mutation.ToAdd[1]);
            Assert.Equal(paragraph.Id, item.ParagraphId);
            return paragraph;
        }

        // ── where a new paragraph lands ─────────────────────────────────────────

        [Fact]
        public void ParagraphBefore_TheFirst_LandsFirstInTheSameChapter()
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Paragraph, b.First.Id, Placement.Before, null, "New."));

            Assert.Equal(b.Full.Id, added.ChapterId);
            Assert.True(Before(added.Order, b.First.Order));
        }

        [Fact]
        public void ParagraphAfter_AParagraph_LandsBetweenItAndTheNext()
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Paragraph, b.First.Id, Placement.After, null, "New."));

            Assert.Equal(b.Full.Id, added.ChapterId);
            Assert.True(Before(b.First.Order, added.Order));
            Assert.True(Before(added.Order, b.Pause.Order));
        }

        [Theory]
        [InlineData(Placement.Before)]
        [InlineData(Placement.After)]
        public void PauseParagraph_TakesBeforeAndAfterToo(Placement placement)
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Paragraph, b.Pause.Id, placement, null, "New."));

            Assert.Equal(b.Full.Id, added.ChapterId);
            var (lower, upper) = placement == Placement.Before ? (b.First, b.Pause) : (b.Pause, b.Last);
            Assert.True(Before(lower.Order, added.Order));
            Assert.True(Before(added.Order, upper.Order));
        }

        [Fact]
        public void ParagraphAfter_TheLast_LandsLastInTheSameChapter()
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Paragraph, b.Last.Id, Placement.After, null, "New."));

            Assert.Equal(b.Full.Id, added.ChapterId);
            Assert.True(Before(b.Last.Order, added.Order));
        }

        [Fact]
        public void ChapterAtStart_LandsBeforeItsFirstParagraph()
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Chapter, b.Full.Id, Placement.AtStart, null, "New."));

            Assert.Equal(b.Full.Id, added.ChapterId);
            Assert.True(Before(added.Order, b.First.Order));
        }

        [Fact]
        public void ChapterAtEnd_LandsAfterItsLastParagraph()
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Chapter, b.Full.Id, Placement.AtEnd, null, "New."));

            Assert.Equal(b.Full.Id, added.ChapterId);
            Assert.True(Before(b.Last.Order, added.Order));
        }

        [Theory]
        [InlineData(Placement.AtStart)]
        [InlineData(Placement.AtEnd)]
        public void AnEmptyChapter_CanBeGivenItsFirstParagraph(Placement placement)
        {
            var b = MakeBook();

            var added = AddedParagraph(b.H.PlanInsertNode(NodeKind.Chapter, b.Empty.Id, placement, null, "New."));

            Assert.Equal(b.Empty.Id, added.ChapterId);
            Assert.False(string.IsNullOrEmpty(added.Order));
        }

        // ── what a new paragraph holds ──────────────────────────────────────────

        [Fact]
        public void NewParagraph_HoldsOneUnattributedSpeechItem_WithTheTrimmedText()
        {
            var b = MakeBook();

            var plan = b.H.PlanInsertNode(NodeKind.Paragraph, b.First.Id, Placement.After, null, "  A restored line.  ");

            AddedParagraph(plan);
            var item = (ParagraphItem)plan!.ToAdd[1];
            Assert.Equal(ParagraphItemType.Speech, item.ItemType);
            Assert.Equal("A restored line.", item.Text);
            Assert.Null(item.CharacterId);
            Assert.Null(item.VoiceInstructions);
            Assert.Null(item.AudioFileName);
            Assert.False(string.IsNullOrEmpty(item.Order));
        }

        // ── refusals ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n ")]
        public void BlankText_IsRefused(string? text)
        {
            var b = MakeBook();

            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, b.First.Id, Placement.After, null, text));
            Assert.Null(b.H.PlanInsertNode(NodeKind.Chapter, b.Empty.Id, Placement.AtStart, null, text));
        }

        [Fact]
        public void AnUnknownAnchor_IsRefused()
        {
            var b = MakeBook();

            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, Guid.NewGuid(), Placement.After, null, "New."));
            Assert.Null(b.H.PlanInsertNode(NodeKind.Chapter, Guid.NewGuid(), Placement.AtEnd, null, "New."));
        }

        [Fact]
        public void AnAnchorOfTheWrongKind_IsRefused()
        {
            var b = MakeBook();

            // A chapter's id named as a paragraph is not a paragraph, whatever else it is.
            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, b.Full.Id, Placement.After, null, "New."));
            Assert.Null(b.H.PlanInsertNode(NodeKind.Chapter, b.First.Id, Placement.AtEnd, null, "New."));
        }

        // ── which kind a combination creates ─────────────────────────────────────

        [Theory]
        [InlineData(NodeKind.Volume, Placement.Before, NodeKind.Volume)]
        [InlineData(NodeKind.Volume, Placement.After, NodeKind.Volume)]
        [InlineData(NodeKind.Volume, Placement.AtStart, NodeKind.Part)]
        [InlineData(NodeKind.Volume, Placement.AtEnd, NodeKind.Part)]
        [InlineData(NodeKind.Part, Placement.Before, NodeKind.Part)]
        [InlineData(NodeKind.Part, Placement.AtEnd, NodeKind.Chapter)]
        [InlineData(NodeKind.Chapter, Placement.After, NodeKind.Chapter)]
        [InlineData(NodeKind.Chapter, Placement.AtStart, NodeKind.Paragraph)]
        [InlineData(NodeKind.Paragraph, Placement.Before, NodeKind.Paragraph)]
        [InlineData(NodeKind.Paragraph, Placement.AtEnd, NodeKind.ParagraphItem)]
        public void TheNewKind_FollowsFromTheAnchorAndThePlacement(NodeKind anchor, Placement placement, NodeKind expected)
        {
            Assert.Equal(expected, BookHierarchy.InsertedKind(anchor, placement));
        }

        [Theory]
        [InlineData(Placement.Before)]
        [InlineData(Placement.After)]
        [InlineData(Placement.AtStart)]
        [InlineData(Placement.AtEnd)]
        public void AnItemAnchor_CreatesNothing(Placement placement)
        {
            // Before/After an item is the existing item insert; an item has no children.
            Assert.Null(BookHierarchy.InsertedKind(NodeKind.ParagraphItem, placement));
        }
    }
}
