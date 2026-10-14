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

        // ── a speech item at a paragraph's start or end ─────────────────────────

        /// <summary>
        /// The book's first paragraph, reshaped to open and close on a pause item with speech between:
        /// neither end can anchor an item insert, so only the paragraph's own start and end reach them.
        /// </summary>
        private static (BookHierarchy H, Paragraph P, ParagraphItem Lead, ParagraphItem Trail) MakePausedParagraph()
        {
            var b = MakeBook();
            var p = b.First;
            var lead = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = p.Id, Order = Key(), ItemType = ParagraphItemType.Pause };
            var speech = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = p.Id, Order = Key(lead.Order), ItemType = ParagraphItemType.Speech, Text = "Spoken." };
            var trail = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = p.Id, Order = Key(speech.Order), ItemType = ParagraphItemType.ParagraphPause };
            b.H.Items[p.Id] = [lead, speech, trail];
            return (b.H, p, lead, trail);
        }

        [Fact]
        public void ItemAtStart_LandsAheadOfALeadingPause()
        {
            var (h, p, lead, _) = MakePausedParagraph();

            var item = AddedNode<ParagraphItem>(h.PlanInsertNode(NodeKind.Paragraph, p.Id, Placement.AtStart, null, "New."));

            Assert.Equal(p.Id, item.ParagraphId);
            Assert.True(Before(item.Order, lead.Order));
        }

        [Fact]
        public void ItemAtEnd_LandsAfterATrailingPause()
        {
            var (h, p, _, trail) = MakePausedParagraph();

            var item = AddedNode<ParagraphItem>(h.PlanInsertNode(NodeKind.Paragraph, p.Id, Placement.AtEnd, null, "New."));

            Assert.Equal(p.Id, item.ParagraphId);
            Assert.True(Before(trail.Order, item.Order));
        }

        [Theory]
        [InlineData(Placement.AtStart)]
        [InlineData(Placement.AtEnd)]
        public void NewItem_IsAnUnattributedSpeechItem_WithTheTrimmedText(Placement placement)
        {
            var b = MakeBook();

            var item = AddedNode<ParagraphItem>(b.H.PlanInsertNode(NodeKind.Paragraph, b.Last.Id, placement, null, "  Added.  "));

            Assert.Equal(ParagraphItemType.Speech, item.ItemType);
            Assert.Equal("Added.", item.Text);
            Assert.Null(item.CharacterId);
            Assert.Null(item.VoiceInstructions);
            Assert.Null(item.AudioFileName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ItemAtAnEnd_WithBlankText_IsRefused(string? text)
        {
            var b = MakeBook();

            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, b.Last.Id, Placement.AtStart, null, text));
            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, b.Last.Id, Placement.AtEnd, null, text));
        }

        [Theory]
        [InlineData(Placement.AtStart)]
        [InlineData(Placement.AtEnd)]
        public void APauseParagraph_TakesNoItem(Placement placement)
        {
            // Speech inside a pause paragraph is a structure the readers assume cannot exist; its
            // inserts are Paragraph before/after.
            var b = MakeBook();

            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, b.Pause.Id, placement, null, "New."));
        }

        [Fact]
        public void ItemAtAnEnd_OfAnUnknownParagraph_IsRefused()
        {
            var b = MakeBook();

            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, Guid.NewGuid(), Placement.AtEnd, null, "New."));
            Assert.Null(b.H.PlanInsertNode(NodeKind.Paragraph, b.Full.Id, Placement.AtStart, null, "New."));
        }

        // ── volumes, parts and chapters ─────────────────────────────────────────

        private sealed record Shelf(
            BookHierarchy H, Volume First, Volume EmptyVolume, Part Full, Part EmptyPart, Chapter Opening, Chapter Closing);

        /// <summary>
        /// Two volumes: the first holds a part with two chapters and an empty part; the second is
        /// empty. Every container a new node can land beside, at either end of, or inside while empty.
        /// </summary>
        private static Shelf MakeShelf()
        {
            var first = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "One" };
            var emptyVolume = new Volume { Id = Guid.NewGuid(), Order = Key(first.Order), Title = "Two" };
            var full = new Part { Id = Guid.NewGuid(), VolumeId = first.Id, Order = Key(), Title = "Part" };
            var emptyPart = new Part { Id = Guid.NewGuid(), VolumeId = first.Id, Order = Key(full.Order) };
            var opening = new Chapter { Id = Guid.NewGuid(), PartId = full.Id, Order = Key(), Title = "Opening" };
            var closing = new Chapter { Id = Guid.NewGuid(), PartId = full.Id, Order = Key(opening.Order), Title = "Closing" };

            var h = new BookHierarchy
            {
                Volumes = [first, emptyVolume],
                Parts = { [first.Id] = [full, emptyPart] },
                Chapters = { [full.Id] = [opening, closing] },
            };
            return new Shelf(h, first, emptyVolume, full, emptyPart, opening, closing);
        }

        /// <summary>The plan's one new node, after asserting it adds that node and nothing else — born empty.</summary>
        private static T AddedNode<T>(HierarchyMutation? mutation)
        {
            Assert.NotNull(mutation);
            Assert.Empty(mutation!.ToDelete);
            Assert.Empty(mutation.ToUpdate);
            return Assert.IsType<T>(Assert.Single(mutation.ToAdd));
        }

        [Fact]
        public void VolumeBefore_TheFirst_LandsFirst()
        {
            var s = MakeShelf();

            var added = AddedNode<Volume>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.Before, null, null));

            Assert.True(Before(added.Order, s.First.Order));
        }

        [Fact]
        public void VolumeAfter_AVolume_LandsBetweenItAndTheNext()
        {
            var s = MakeShelf();

            var added = AddedNode<Volume>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.After, null, null));

            Assert.True(Before(s.First.Order, added.Order));
            Assert.True(Before(added.Order, s.EmptyVolume.Order));
        }

        [Fact]
        public void VolumeAfter_TheLast_LandsLast()
        {
            var s = MakeShelf();

            var added = AddedNode<Volume>(s.H.PlanInsertNode(NodeKind.Volume, s.EmptyVolume.Id, Placement.After, null, null));

            Assert.True(Before(s.EmptyVolume.Order, added.Order));
        }

        [Fact]
        public void PartAtStart_LandsBeforeTheVolumesFirstPart()
        {
            var s = MakeShelf();

            var added = AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.AtStart, null, null));

            Assert.Equal(s.First.Id, added.VolumeId);
            Assert.True(Before(added.Order, s.Full.Order));
        }

        [Fact]
        public void PartAtEnd_LandsAfterTheVolumesLastPart()
        {
            var s = MakeShelf();

            var added = AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.AtEnd, null, null));

            Assert.Equal(s.First.Id, added.VolumeId);
            Assert.True(Before(s.EmptyPart.Order, added.Order));
        }

        [Theory]
        [InlineData(Placement.AtStart)]
        [InlineData(Placement.AtEnd)]
        public void AnEmptyVolume_CanBeGivenItsFirstPart(Placement placement)
        {
            var s = MakeShelf();

            var added = AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Volume, s.EmptyVolume.Id, placement, null, null));

            Assert.Equal(s.EmptyVolume.Id, added.VolumeId);
            Assert.False(string.IsNullOrEmpty(added.Order));
        }

        [Fact]
        public void PartBefore_APart_LandsBetweenItAndThePrevious()
        {
            var s = MakeShelf();

            var added = AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Part, s.EmptyPart.Id, Placement.Before, null, null));

            Assert.Equal(s.First.Id, added.VolumeId);
            Assert.True(Before(s.Full.Order, added.Order));
            Assert.True(Before(added.Order, s.EmptyPart.Order));
        }

        [Fact]
        public void PartAfter_TheLast_LandsLastInTheSameVolume()
        {
            var s = MakeShelf();

            var added = AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Part, s.EmptyPart.Id, Placement.After, null, null));

            Assert.Equal(s.First.Id, added.VolumeId);
            Assert.True(Before(s.EmptyPart.Order, added.Order));
        }

        [Fact]
        public void ChapterAtStart_LandsBeforeThePartsFirstChapter()
        {
            var s = MakeShelf();

            var added = AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Part, s.Full.Id, Placement.AtStart, null, null));

            Assert.Equal(s.Full.Id, added.PartId);
            Assert.True(Before(added.Order, s.Opening.Order));
        }

        [Fact]
        public void ChapterAtEnd_LandsAfterThePartsLastChapter()
        {
            var s = MakeShelf();

            var added = AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Part, s.Full.Id, Placement.AtEnd, null, null));

            Assert.Equal(s.Full.Id, added.PartId);
            Assert.True(Before(s.Closing.Order, added.Order));
        }

        [Theory]
        [InlineData(Placement.AtStart)]
        [InlineData(Placement.AtEnd)]
        public void AnEmptyPart_CanBeGivenItsFirstChapter(Placement placement)
        {
            var s = MakeShelf();

            var added = AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Part, s.EmptyPart.Id, placement, null, null));

            Assert.Equal(s.EmptyPart.Id, added.PartId);
            Assert.False(string.IsNullOrEmpty(added.Order));
        }

        [Fact]
        public void ChapterBefore_TheFirst_LandsFirstInTheSamePart()
        {
            var s = MakeShelf();

            var added = AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Chapter, s.Opening.Id, Placement.Before, null, null));

            Assert.Equal(s.Full.Id, added.PartId);
            Assert.True(Before(added.Order, s.Opening.Order));
        }

        [Fact]
        public void ChapterAfter_AChapter_LandsBetweenItAndTheNext()
        {
            var s = MakeShelf();

            var added = AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Chapter, s.Opening.Id, Placement.After, null, null));

            Assert.Equal(s.Full.Id, added.PartId);
            Assert.True(Before(s.Opening.Order, added.Order));
            Assert.True(Before(added.Order, s.Closing.Order));
        }

        [Fact]
        public void ANewContainer_TakesTheTrimmedTitle()
        {
            var s = MakeShelf();

            Assert.Equal("Prologue", AddedNode<Volume>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.Before, "  Prologue ", null)).Title);
            Assert.Equal("Book Two", AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.AtEnd, "Book Two", null)).Title);
            Assert.Equal("Interlude", AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Chapter, s.Opening.Id, Placement.After, "Interlude", null)).Title);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ABlankTitle_LeavesTheNewContainerUntitled(string? title)
        {
            var s = MakeShelf();

            // A Volume's title is not nullable, so untitled is empty there; a Part's and a
            // Chapter's is null, as an imported untitled one is.
            Assert.Equal("", AddedNode<Volume>(s.H.PlanInsertNode(NodeKind.Volume, s.First.Id, Placement.After, title, null)).Title);
            Assert.Null(AddedNode<Part>(s.H.PlanInsertNode(NodeKind.Part, s.Full.Id, Placement.After, title, null)).Title);
            Assert.Null(AddedNode<Chapter>(s.H.PlanInsertNode(NodeKind.Part, s.Full.Id, Placement.AtStart, title, null)).Title);
        }

        [Theory]
        [InlineData(NodeKind.Volume, Placement.Before)]
        [InlineData(NodeKind.Volume, Placement.AtStart)]
        [InlineData(NodeKind.Part, Placement.After)]
        [InlineData(NodeKind.Part, Placement.AtEnd)]
        [InlineData(NodeKind.Chapter, Placement.Before)]
        public void AnUnknownContainerAnchor_IsRefused(NodeKind anchor, Placement placement)
        {
            var s = MakeShelf();

            Assert.Null(s.H.PlanInsertNode(anchor, Guid.NewGuid(), placement, null, null));
        }

        [Fact]
        public void AContainerAnchorOfTheWrongKind_IsRefused()
        {
            var s = MakeShelf();

            Assert.Null(s.H.PlanInsertNode(NodeKind.Volume, s.Full.Id, Placement.After, null, null));
            Assert.Null(s.H.PlanInsertNode(NodeKind.Part, s.First.Id, Placement.AtStart, null, null));
            Assert.Null(s.H.PlanInsertNode(NodeKind.Chapter, s.Full.Id, Placement.Before, null, null));
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
