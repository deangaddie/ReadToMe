using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class BookHierarchyMergeTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        // ── Volume merge ─────────────────────────────────────────────────────────

        private static (BookHierarchy h, Volume v1, Volume v2) TwoVolumeHierarchy()
        {
            var v1 = new Volume { Id = Guid.NewGuid(), Title = "V1", Order = Key() };
            var v2 = new Volume { Id = Guid.NewGuid(), Title = "V2", Order = Key(v1.Order) };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = v1.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = v2.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [v1, v2],
                Parts = { [v1.Id] = [p1], [v2.Id] = [p2] },
                Chapters = { [p1.Id] = [], [p2.Id] = [] },
            };
            return (h, v1, v2);
        }

        [Fact]
        public void PlanMergeVolume_Previous_FoldsLoserPartsIntoWinner_AndDeletesLoser()
        {
            var (h, v1, v2) = TwoVolumeHierarchy();
            var loserPart = h.Parts[v2.Id][0];

            var plan = h.PlanMergeVolume(v2.Id, MergeDirection.Previous);

            Assert.NotNull(plan);
            Assert.Contains(v2, plan!.Mutation.ToDelete);
            Assert.Contains(loserPart, plan.Mutation.ToUpdate);
            Assert.Equal(v1.Id, loserPart.VolumeId);
            // The pair a Book View carries expansion by: what went, and what took its place.
            Assert.Equal(v1.Id, plan.SurvivorId);
            Assert.Equal(v2.Id, plan.DeletedId);
        }

        [Fact]
        public void PlanMergeVolume_Previous_FirstVolume_ReturnsNull()
        {
            var (h, v1, _) = TwoVolumeHierarchy();
            Assert.Null(h.PlanMergeVolume(v1.Id, MergeDirection.Previous));
        }

        [Fact]
        public void PlanMergeVolume_Next_LastVolume_ReturnsNull()
        {
            var (h, _, v2) = TwoVolumeHierarchy();
            Assert.Null(h.PlanMergeVolume(v2.Id, MergeDirection.Next));
        }

        // ── Part merge ───────────────────────────────────────────────────────────

        private static (BookHierarchy h, Part p1, Part p2) TwoPartHierarchy()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key(p1.Order) };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = p2.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [p1, p2] },
                Chapters = { [p1.Id] = [c1], [p2.Id] = [c2] },
                Paragraphs = { [c1.Id] = [], [c2.Id] = [] },
            };
            return (h, p1, p2);
        }

        [Fact]
        public void PlanMergePart_Previous_FoldsLoserChaptersIntoWinner_AndDeletesLoser()
        {
            var (h, p1, p2) = TwoPartHierarchy();
            var loserChapter = h.Chapters[p2.Id][0];

            var plan = h.PlanMergePart(p2.Id, MergeDirection.Previous);

            Assert.NotNull(plan);
            Assert.Contains(p2, plan!.Mutation.ToDelete);
            Assert.Contains(loserChapter, plan.Mutation.ToUpdate);
            Assert.Equal(p1.Id, loserChapter.PartId);
            Assert.Equal(p1.Id, plan.SurvivorId);
            Assert.Equal(p2.Id, plan.DeletedId);
        }

        [Fact]
        public void PlanMergePart_Previous_FirstPart_ReturnsNull()
        {
            var (h, p1, _) = TwoPartHierarchy();
            Assert.Null(h.PlanMergePart(p1.Id, MergeDirection.Previous));
        }

        [Fact]
        public void PlanMergePart_Next_LastPart_ReturnsNull()
        {
            var (h, _, p2) = TwoPartHierarchy();
            Assert.Null(h.PlanMergePart(p2.Id, MergeDirection.Next));
        }

        // ── Chapter merge ────────────────────────────────────────────────────────

        private static (BookHierarchy h, Chapter c1, Chapter c2) TwoChapterHierarchy()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key(c1.Order) };
            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key() };
            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c2.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [c1, c2] },
                Paragraphs = { [c1.Id] = [pg1], [c2.Id] = [pg2] },
                Items = { [pg1.Id] = [], [pg2.Id] = [] },
            };
            return (h, c1, c2);
        }

        [Fact]
        public void PlanMergeChapter_Previous_FoldsLoserParagraphsIntoWinner_AndDeletesLoser()
        {
            var (h, c1, c2) = TwoChapterHierarchy();
            var loserPara = h.Paragraphs[c2.Id][0];

            var plan = h.PlanMergeChapter(c2.Id, MergeDirection.Previous);

            Assert.NotNull(plan);
            Assert.Contains(c2, plan!.Mutation.ToDelete);
            Assert.Contains(loserPara, plan.Mutation.ToUpdate);
            Assert.Equal(c1.Id, loserPara.ChapterId);
            Assert.Equal(c1.Id, plan.SurvivorId);
            Assert.Equal(c2.Id, plan.DeletedId);
        }

        [Fact]
        public void PlanMergeChapter_Previous_FirstChapter_ReturnsNull()
        {
            var (h, c1, _) = TwoChapterHierarchy();
            Assert.Null(h.PlanMergeChapter(c1.Id, MergeDirection.Previous));
        }

        [Fact]
        public void PlanMergeChapter_Next_LastChapter_ReturnsNull()
        {
            var (h, _, c2) = TwoChapterHierarchy();
            Assert.Null(h.PlanMergeChapter(c2.Id, MergeDirection.Next));
        }

        // ── Paragraph merge ──────────────────────────────────────────────────────

        private static (BookHierarchy h, Paragraph pg1, Paragraph pg2) TwoParagraphHierarchy()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(pg1.Order) };
            var i1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg1.Id, Order = Key() };
            var i2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg2.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg1, pg2] },
                Items = { [pg1.Id] = [i1], [pg2.Id] = [i2] },
            };
            return (h, pg1, pg2);
        }

        [Fact]
        public void PlanMergeParagraph_Previous_FoldsLoserItemsIntoWinner_AndDeletesLoser()
        {
            var (h, pg1, pg2) = TwoParagraphHierarchy();
            var loserItem = h.Items[pg2.Id][0];

            var plan = h.PlanMergeParagraph(pg2.Id, MergeDirection.Previous);

            Assert.NotNull(plan);
            Assert.Contains(pg2, plan!.Mutation.ToDelete);
            Assert.Contains(loserItem, plan.Mutation.ToUpdate);
            Assert.Equal(pg1.Id, loserItem.ParagraphId);
            Assert.Equal(pg1.Id, plan.SurvivorId);
            Assert.Equal(pg2.Id, plan.DeletedId);
        }

        [Fact]
        public void PlanMergeParagraph_Previous_FirstParagraph_ReturnsNull()
        {
            var (h, pg1, _) = TwoParagraphHierarchy();
            Assert.Null(h.PlanMergeParagraph(pg1.Id, MergeDirection.Previous));
        }

        [Fact]
        public void PlanMergeParagraph_Next_LastParagraph_ReturnsNull()
        {
            var (h, _, pg2) = TwoParagraphHierarchy();
            Assert.Null(h.PlanMergeParagraph(pg2.Id, MergeDirection.Next));
        }

        // ── ParagraphItem merge ──────────────────────────────────────────────────

        private static (BookHierarchy h, ParagraphItem i1, ParagraphItem i2) TwoItemHierarchy(string text1, string text2)
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var pg = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var i1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg.Id, Order = Key(), Text = text1 };
            var i2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg.Id, Order = Key(i1.Order), Text = text2 };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg] },
                Items = { [pg.Id] = [i1, i2] },
            };
            return (h, i1, i2);
        }

        [Fact]
        public void PlanMergeParagraphItem_Next_ConcatenatesTextWithSingleSpace()
        {
            var (h, i1, i2) = TwoItemHierarchy("Hello", "world");
            var plan = h.PlanMergeParagraphItem(i1.Id, MergeDirection.Next);

            Assert.NotNull(plan);
            Assert.Contains(i2, plan!.Mutation.ToDelete);
            Assert.Contains(i1, plan.Mutation.ToUpdate);
            Assert.Equal("Hello world", i1.Text);
            Assert.Equal(i1.Id, plan.SurvivorId);
            Assert.Equal(i2.Id, plan.DeletedId);
        }

        [Fact]
        public void PlanMergeParagraphItem_Previous_BlankSurvivor_TakesLoserText()
        {
            var (h, i1, i2) = TwoItemHierarchy("  ", "loser text");
            var plan = h.PlanMergeParagraphItem(i2.Id, MergeDirection.Previous);

            Assert.NotNull(plan);
            Assert.Equal("loser text", i1.Text);
        }

        [Fact]
        public void PlanMergeParagraphItem_FirstItem_Previous_ReturnsNull()
        {
            var (h, i1, _) = TwoItemHierarchy("a", "b");
            Assert.Null(h.PlanMergeParagraphItem(i1.Id, MergeDirection.Previous));
        }
    }
}
