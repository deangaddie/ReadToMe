using System;
using System.Collections.Generic;
using System.Linq;
using FractionalIndexing;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class BookHierarchyTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        // ---------------------------------------------------------------
        // Helpers: build minimal hierarchies
        // ---------------------------------------------------------------

        private static (Volume v, Part p1, Part p2) MakeTwoParts()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "P1", Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "P2", Order = Key(Key()) };
            return (vol, p1, p2);
        }

        private static BookHierarchy HierarchyWithParts(Volume vol, Part p1, Part p2)
            => new()
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [p1, p2] },
                Chapters = { [p1.Id] = [], [p2.Id] = [] },
            };

        private static (Part part, Chapter c1, Chapter c2) MakeTwoChapters(Guid volumeId)
        {
            var part = new Part { Id = Guid.NewGuid(), VolumeId = volumeId, Title = "Part", Order = Key() };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "C1", Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "C2", Order = Key(Key()) };
            return (part, c1, c2);
        }

        private static (Chapter ch, Paragraph pg1, Paragraph pg2) MakeTwoParagraphs(Guid partId)
        {
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = partId, Title = "Ch", Order = Key() };
            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(Key()) };
            return (ch, pg1, pg2);
        }

        private static (Paragraph pg, ParagraphItem i1, ParagraphItem i2) MakeTwoItems(Guid chapterId)
        {
            var pg = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapterId, Order = Key() };
            var i1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg.Id, Order = Key() };
            var i2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = pg.Id, Order = Key(Key()) };
            return (pg, i1, i2);
        }

        // ---------------------------------------------------------------
        // PlanSplitVolume
        // ---------------------------------------------------------------

        [Fact]
        public void PlanSplitVolume_SplitsAtPart_NewVolumeAdded_PartsReassigned()
        {
            var (vol, p1, p2) = MakeTwoParts();
            var h = HierarchyWithParts(vol, p1, p2);

            var mutation = h.PlanSplitVolume(p2.Id, "Vol2");

            Assert.NotNull(mutation);
            Assert.Single(mutation.ToAdd);
            Assert.Empty(mutation.ToDelete);
            Assert.Contains(p2, mutation.ToUpdate.Cast<Part>());

            var newVol = (Volume)mutation.ToAdd[0];
            Assert.Equal("Vol2", newVol.Title);
            Assert.Equal(newVol.Id, p2.VolumeId);
            Assert.Equal(vol.Id, p1.VolumeId); // p1 untouched
        }

        [Fact]
        public void PlanSplitVolume_PartNotFound_ReturnsNull()
        {
            var (vol, p1, p2) = MakeTwoParts();
            var h = HierarchyWithParts(vol, p1, p2);

            Assert.Null(h.PlanSplitVolume(Guid.NewGuid(), null));
        }

        [Fact]
        public void PlanSplitVolume_NullTitle_InheritsCurrentVolumeTitle()
        {
            var (vol, p1, p2) = MakeTwoParts();
            var h = HierarchyWithParts(vol, p1, p2);

            var mutation = h.PlanSplitVolume(p2.Id, null);

            var newVol = (Volume)mutation!.ToAdd[0];
            Assert.Equal(vol.Title, newVol.Title);
        }

        [Fact]
        public void PlanSplitVolume_SplitAtFirst_MovesAllParts()
        {
            var (vol, p1, p2) = MakeTwoParts();
            var h = HierarchyWithParts(vol, p1, p2);

            var mutation = h.PlanSplitVolume(p1.Id, "NewVol");

            Assert.NotNull(mutation);
            var newVol = (Volume)mutation!.ToAdd[0];
            Assert.Equal(2, mutation.ToUpdate.Count);
            Assert.All(mutation.ToUpdate.Cast<Part>(), p => Assert.Equal(newVol.Id, p.VolumeId));
        }

        // ---------------------------------------------------------------
        // PlanSplitPart
        // ---------------------------------------------------------------

        [Fact]
        public void PlanSplitPart_SplitsAtChapter_NewPartAdded_ChaptersReassigned()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var (part, c1, c2) = MakeTwoChapters(vol.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [c1, c2] },
            };

            var mutation = h.PlanSplitPart(c2.Id, "Part2");

            Assert.NotNull(mutation);
            var newPart = (Part)mutation!.ToAdd[0];
            Assert.Equal("Part2", newPart.Title);
            Assert.Equal(newPart.Id, c2.PartId);
            Assert.Equal(part.Id, c1.PartId);
        }

        [Fact]
        public void PlanSplitPart_ChapterNotFound_ReturnsNull()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var (part, c1, _) = MakeTwoChapters(vol.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [c1] },
            };

            Assert.Null(h.PlanSplitPart(Guid.NewGuid(), null));
        }

        // ---------------------------------------------------------------
        // PlanSplitChapter
        // ---------------------------------------------------------------

        [Fact]
        public void PlanSplitChapter_SplitsAtParagraph_NewChapterAdded_ParagraphsReassigned()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var (ch, pg1, pg2) = MakeTwoParagraphs(part.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg1, pg2] },
            };

            var mutation = h.PlanSplitChapter(pg2.Id, "Ch2");

            Assert.NotNull(mutation);
            var newCh = (Chapter)mutation!.ToAdd[0];
            Assert.Equal("Ch2", newCh.Title);
            Assert.Equal(newCh.Id, pg2.ChapterId);
            Assert.Equal(ch.Id, pg1.ChapterId);
        }

        [Fact]
        public void PlanSplitChapter_ParagraphNotFound_ReturnsNull()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var (ch, pg1, _) = MakeTwoParagraphs(part.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg1] },
            };

            Assert.Null(h.PlanSplitChapter(Guid.NewGuid(), null));
        }

        [Fact]
        public void PlanSplitChapter_NewChapterInheritsPartId()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var (ch, pg1, pg2) = MakeTwoParagraphs(part.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg1, pg2] },
            };

            var mutation = h.PlanSplitChapter(pg2.Id, null);

            var newCh = (Chapter)mutation!.ToAdd[0];
            Assert.Equal(ch.PartId, newCh.PartId);
        }

        // ---------------------------------------------------------------
        // PlanSplitParagraph
        // ---------------------------------------------------------------

        [Fact]
        public void PlanSplitParagraph_SplitsAtItem_NewParagraphAdded_ItemsReassigned()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var (pg, i1, i2) = MakeTwoItems(ch.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg] },
                Items = { [pg.Id] = [i1, i2] },
            };

            var mutation = h.PlanSplitParagraph(i2.Id);

            Assert.NotNull(mutation);
            var newPg = (Paragraph)mutation!.ToAdd[0];
            Assert.Equal(newPg.Id, i2.ParagraphId);
            Assert.Equal(pg.Id, i1.ParagraphId);
        }

        [Fact]
        public void PlanSplitParagraph_ItemNotFound_ReturnsNull()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var (pg, i1, _) = MakeTwoItems(ch.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg] },
                Items = { [pg.Id] = [i1] },
            };

            Assert.Null(h.PlanSplitParagraph(Guid.NewGuid()));
        }

        [Fact]
        public void PlanSplitParagraph_NewParagraphInheritsChapterId()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var (pg, i1, i2) = MakeTwoItems(ch.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg] },
                Items = { [pg.Id] = [i1, i2] },
            };

            var mutation = h.PlanSplitParagraph(i2.Id);

            var newPg = (Paragraph)mutation!.ToAdd[0];
            Assert.Equal(pg.ChapterId, newPg.ChapterId);
        }

        [Fact]
        public void PlanSplitParagraph_SplitAtFirst_MovesAllItems()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key(), Title = "Vol" };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var (pg, i1, i2) = MakeTwoItems(ch.Id);
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg] },
                Items = { [pg.Id] = [i1, i2] },
            };

            var mutation = h.PlanSplitParagraph(i1.Id);

            Assert.NotNull(mutation);
            var newPg = (Paragraph)mutation!.ToAdd[0];
            Assert.Equal(2, mutation.ToUpdate.Count);
            Assert.All(mutation.ToUpdate.Cast<ParagraphItem>(), i => Assert.Equal(newPg.Id, i.ParagraphId));
        }
    }
}
