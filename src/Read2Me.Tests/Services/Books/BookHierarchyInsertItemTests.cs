using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    /// <summary>
    /// PlanInsertParagraphItem — the ordering and refusals of manual item insertion, decided
    /// entirely in memory. The load-bearing assertion is that the new item is born unattributed:
    /// the anchor held two speakers by construction, so an inherited speaker would be a confident
    /// wrong answer that never reaches the attribution queue.
    /// </summary>
    public class BookHierarchyInsertItemTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        private static bool Before(string a, string b) => string.Compare(a, b, StringComparison.Ordinal) < 0;

        /// <summary>
        /// One chapter, two paragraphs: the first holds two Speech items, the second holds one, so
        /// a plan that reached across the Paragraph boundary would show up as a wrong parent.
        /// </summary>
        private static (BookHierarchy h, Paragraph pg1, ParagraphItem i1, ParagraphItem i2, Paragraph pg2, ParagraphItem i3) MakeChapter()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };

            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var o1 = Key();
            var o2 = Key(o1);
            var i1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg1.Id, Order = o1, ItemType = ParagraphItemType.Speech, Text = "First." };
            var i2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg1.Id, Order = o2, ItemType = ParagraphItemType.Speech, Text = "Second." };

            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(pg1.Order) };
            var i3 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg2.Id, Order = Key(), ItemType = ParagraphItemType.Speech, Text = "Next paragraph." };

            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg1, pg2] },
                Items = { [pg1.Id] = [i1, i2], [pg2.Id] = [i3] },
            };
            return (h, pg1, i1, i2, pg2, i3);
        }

        private static ParagraphItem Added(HierarchyMutation? mutation)
        {
            Assert.NotNull(mutation);
            Assert.Single(mutation!.ToAdd);
            Assert.Empty(mutation.ToDelete);
            Assert.Empty(mutation.ToUpdate);
            return (ParagraphItem)mutation.ToAdd[0];
        }

        [Fact]
        public void Before_FirstItem_LandsFirstInTheSameParagraph()
        {
            var (h, pg1, i1, _, _, _) = MakeChapter();

            var added = Added(h.PlanInsertParagraphItem(i1.Id, InsertPosition.Before, "New line."));

            Assert.Equal(pg1.Id, added.ParagraphId);
            Assert.True(Before(added.Order, i1.Order));
        }

        [Fact]
        public void After_LastItem_LandsLastInTheSameParagraph()
        {
            var (h, pg1, _, i2, pg2, i3) = MakeChapter();

            var added = Added(h.PlanInsertParagraphItem(i2.Id, InsertPosition.After, "New line."));

            // Never crosses into the next Paragraph: same parent, and ordering is judged only
            // against its own siblings, so pg2's keys are free to compare either way.
            Assert.Equal(pg1.Id, added.ParagraphId);
            Assert.NotEqual(pg2.Id, added.ParagraphId);
            Assert.True(Before(i2.Order, added.Order));
            Assert.NotEqual(i3.ParagraphId, added.ParagraphId);
        }

        [Fact]
        public void After_FirstOfTwo_LandsBetweenTheSiblings()
        {
            var (h, _, i1, i2, _, _) = MakeChapter();

            var added = Added(h.PlanInsertParagraphItem(i1.Id, InsertPosition.After, "Wedged in."));

            Assert.True(Before(i1.Order, added.Order));
            Assert.True(Before(added.Order, i2.Order));
        }

        [Fact]
        public void Before_SecondOfTwo_LandsBetweenTheSiblings()
        {
            var (h, _, i1, i2, _, _) = MakeChapter();

            var added = Added(h.PlanInsertParagraphItem(i2.Id, InsertPosition.Before, "Wedged in."));

            Assert.True(Before(i1.Order, added.Order));
            Assert.True(Before(added.Order, i2.Order));
        }

        [Fact]
        public void NewItem_IsSpeech_Unattributed_AndTrimmed()
        {
            var (h, _, i1, _, _, _) = MakeChapter();

            var added = Added(h.PlanInsertParagraphItem(i1.Id, InsertPosition.After, "  And who might you be?  "));

            Assert.Equal(ParagraphItemType.Speech, added.ItemType);
            Assert.Equal("And who might you be?", added.Text);
            Assert.Null(added.CharacterId);
            Assert.Null(added.VoiceInstructions);
            Assert.Null(added.AudioFileName);
        }

        [Fact]
        public void AnchorWithASpeaker_DoesNotLendItToTheNewItem()
        {
            var (h, _, i1, _, _, _) = MakeChapter();
            i1.CharacterId = Guid.NewGuid();
            i1.VoiceInstructions = "wry";
            i1.AudioFileName = "anchor.wav";

            var added = Added(h.PlanInsertParagraphItem(i1.Id, InsertPosition.After, "Her reply."));

            Assert.Null(added.CharacterId);
            Assert.Null(added.VoiceInstructions);
            Assert.Null(added.AudioFileName);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n ")]
        public void WhitespaceOnlyText_IsRefused(string text)
        {
            var (h, _, i1, _, _, _) = MakeChapter();

            Assert.Null(h.PlanInsertParagraphItem(i1.Id, InsertPosition.After, text));
        }

        [Fact]
        public void PauseAnchor_IsRefused()
        {
            var (h, _, _, _, pg2, _) = MakeChapter();
            var pause = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = pg2.Id,
                Order = Key(),
                ItemType = ParagraphItemType.ParagraphPause,
            };
            h.Items[pg2.Id] = [pause];

            Assert.Null(h.PlanInsertParagraphItem(pause.Id, InsertPosition.Before, "Text."));
            Assert.Null(h.PlanInsertParagraphItem(pause.Id, InsertPosition.After, "Text."));
        }

        [Fact]
        public void UnknownAnchor_IsRefused()
        {
            var (h, _, _, _, _, _) = MakeChapter();

            Assert.Null(h.PlanInsertParagraphItem(Guid.NewGuid(), InsertPosition.After, "Text."));
        }
    }
}
