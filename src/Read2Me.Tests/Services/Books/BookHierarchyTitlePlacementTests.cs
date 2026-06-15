using System;
using System.Linq;
using FractionalIndexing;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class BookHierarchyTitlePlacementTests
    {
        private static string Key(string? after = null) => OrderKeyGenerator.GenerateKeyBetween(after, null);

        // ── PlanFrontMatterInsert ────────────────────────────────────────────────

        [Fact]
        public void PlanFrontMatter_MultipleVolumes_CreatesLeadingVolumePartChapter()
        {
            var v1 = new Volume { Id = Guid.NewGuid(), Title = "V1", Order = Key() };
            var v2 = new Volume { Id = Guid.NewGuid(), Title = "V2", Order = Key(v1.Order) };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = v1.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [v1, v2],
                Parts = { [v1.Id] = [p1], [v2.Id] = [] },
                Chapters = { [p1.Id] = [] },
            };

            var result = h.PlanFrontMatterInsert();

            Assert.NotNull(result);
            var (mutation, chapterId, _) = result!.Value;
            Assert.Equal(3, mutation.ToAdd.Count); // Volume + Part + Chapter
            Assert.IsType<Volume>(mutation.ToAdd[0]);
            Assert.IsType<Part>(mutation.ToAdd[1]);
            Assert.IsType<Chapter>(mutation.ToAdd[2]);
            Assert.Equal(chapterId, ((Chapter)mutation.ToAdd[2]).Id);
            // New volume must sort before v1
            Assert.True(string.Compare(((Volume)mutation.ToAdd[0]).Order, v1.Order, StringComparison.Ordinal) < 0);
        }

        [Fact]
        public void PlanFrontMatter_SingleVolumeMultipleParts_CreatesLeadingPartChapter()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key(p1.Order) };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [p1, p2] },
                Chapters = { [p1.Id] = [c1], [p2.Id] = [] },
            };

            var result = h.PlanFrontMatterInsert();

            Assert.NotNull(result);
            var (mutation, chapterId, _) = result!.Value;
            Assert.Equal(2, mutation.ToAdd.Count); // Part + Chapter
            Assert.IsType<Part>(mutation.ToAdd[0]);
            Assert.IsType<Chapter>(mutation.ToAdd[1]);
            // New part must sort before p1
            Assert.True(string.Compare(((Part)mutation.ToAdd[0]).Order, p1.Order, StringComparison.Ordinal) < 0);
        }

        [Fact]
        public void PlanFrontMatter_SingleVolumeSinglePart_CreatesLeadingChapterOnly()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var existingChapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [existingChapter] },
            };

            var result = h.PlanFrontMatterInsert();

            Assert.NotNull(result);
            var (mutation, _, _) = result!.Value;
            Assert.Single(mutation.ToAdd);
            Assert.IsType<Chapter>(mutation.ToAdd[0]);
            // New chapter must sort before existing chapter
            Assert.True(string.Compare(((Chapter)mutation.ToAdd[0]).Order, existingChapter.Order, StringComparison.Ordinal) < 0);
        }

        [Fact]
        public void PlanFrontMatter_NoVolumes_ReturnsNull()
        {
            var h = new BookHierarchy();
            Assert.Null(h.PlanFrontMatterInsert());
        }

        // ── PlanVolumeTitleChapters ──────────────────────────────────────────────

        [Fact]
        public void PlanTitleChapters_SkipsBlankTitledVolumes()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "  ", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
            };

            var plans = h.PlanVolumeTitleChapters();
            Assert.Empty(plans);
        }

        [Fact]
        public void PlanTitleChapters_OrdersNewTitleChapterBeforeExistingFirstChild()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Volume One", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var existingChapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [existingChapter] },
            };

            var plans = h.PlanVolumeTitleChapters();

            Assert.Single(plans);
            var (_, title, newChapter, _) = plans[0];
            Assert.Equal("Volume One", title);
            Assert.True(string.Compare(newChapter.Order, existingChapter.Order, StringComparison.Ordinal) < 0);
        }

        // ── PlanPartTitleChapters ────────────────────────────────────────────────

        [Fact]
        public void PlanPartTitleChapters_SkipsBlankTitledParts()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = "", Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [] },
            };

            Assert.Empty(h.PlanPartTitleChapters());
        }

        // ── PlanChapterTitleInsertions ───────────────────────────────────────────

        [Fact]
        public void PlanChapterTitleInsertions_SkipsBlankTitledChapters()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = null, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [] },
            };

            Assert.Empty(h.PlanChapterTitleInsertions());
        }

        [Fact]
        public void PlanChapterTitleInsertions_ReturnsFirstParagraphOrderForPositioning()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Chapter One", Order = Key() };
            var pg = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pg] },
            };

            var plans = h.PlanChapterTitleInsertions();

            Assert.Single(plans);
            var (chId, title, firstParagraphOrder) = plans[0];
            Assert.Equal(ch.Id, chId);
            Assert.Equal("Chapter One", title);
            Assert.Equal(pg.Order, firstParagraphOrder);
        }
    }
}
