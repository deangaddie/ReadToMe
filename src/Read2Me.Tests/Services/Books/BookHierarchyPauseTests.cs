using System;
using System.Collections.Generic;
using System.Linq;
using FractionalIndexing;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class BookHierarchyPauseTests
    {
        private static string Key(string? after = null, string? before = null) =>
            OrderKeyGenerator.GenerateKeyBetween(after, before);

        private static ParagraphItem NarrationItem(Guid paragraphId, string order) =>
            new() { Id = Guid.NewGuid(), ParagraphId = paragraphId, ItemType = ParagraphItemType.Narration, Order = order };

        private static ParagraphItem PauseItem(Guid paragraphId, ParagraphItemType type, string order) =>
            new() { Id = Guid.NewGuid(), ParagraphId = paragraphId, ItemType = type, Order = order };

        // ── Test 1: paragraph pauses between each adjacent content pair ──────

        [Fact]
        public void PlanPauseInsertions_BetweenParagraphs_InsertsParagraphPauseBetweenEachPair()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };

            var pA = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var pB = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(pA.Order) };
            var pC = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(pB.Order) };

            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [pA, pB, pC] },
                Items =
                {
                    [pA.Id] = [NarrationItem(pA.Id, Key())],
                    [pB.Id] = [NarrationItem(pB.Id, Key())],
                    [pC.Id] = [NarrationItem(pC.Id, Key())],
                },
            };

            var plan = h.PlanPauseInsertions();

            var paraPauses = plan.Where(p => p.PauseType == ParagraphItemType.ParagraphPause).ToList();
            Assert.Equal(2, paraPauses.Count);

            Assert.Contains(paraPauses, p => p.AfterOrder == pA.Order && p.BeforeOrder == pB.Order);
            Assert.Contains(paraPauses, p => p.AfterOrder == pB.Order && p.BeforeOrder == pC.Order);
        }

        // ── Test 2: single paragraph — no paragraph pause ────────────────────

        [Fact]
        public void PlanPauseInsertions_SingleParagraphChapter_NoParagraphPause()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };

            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [part] },
                Chapters = { [part.Id] = [ch] },
                Paragraphs = { [ch.Id] = [para] },
                Items = { [para.Id] = [NarrationItem(para.Id, Key())] },
            };

            var plan = h.PlanPauseInsertions();

            Assert.Empty(plan.Where(p => p.PauseType == ParagraphItemType.ParagraphPause));
        }

        // ── Test 3: two chapters → ChapterPause on first ─────────────────────

        [Fact]
        public void PlanPauseInsertions_BetweenChapters_InsertsChapterPauseOnPrecedingChapter()
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
                Items =
                {
                    [pg1.Id] = [NarrationItem(pg1.Id, Key())],
                    [pg2.Id] = [NarrationItem(pg2.Id, Key())],
                },
            };

            var plan = h.PlanPauseInsertions();

            var chapterPauses = plan.Where(p => p.PauseType == ParagraphItemType.ChapterPause).ToList();
            Assert.Single(chapterPauses);
            Assert.Equal(c1.Id, chapterPauses[0].ChapterId);
            Assert.Equal(pg1.Order, chapterPauses[0].AfterOrder);
            Assert.Null(chapterPauses[0].BeforeOrder);

            Assert.Empty(plan.Where(p => p.ChapterId == c2.Id && p.PauseType == ParagraphItemType.ChapterPause));
        }

        // ── Test 4: two parts → PartPause on last chapter of first part ──────

        [Fact]
        public void PlanPauseInsertions_BetweenParts_InsertsPartPauseOnLastChapterOfPrecedingPart()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key(p1.Order) };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = p2.Id, Order = Key() };
            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key() };
            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c2.Id, Order = Key() };

            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [p1, p2] },
                Chapters = { [p1.Id] = [c1], [p2.Id] = [c2] },
                Paragraphs = { [c1.Id] = [pg1], [c2.Id] = [pg2] },
                Items =
                {
                    [pg1.Id] = [NarrationItem(pg1.Id, Key())],
                    [pg2.Id] = [NarrationItem(pg2.Id, Key())],
                },
            };

            var plan = h.PlanPauseInsertions();

            var partPauses = plan.Where(p => p.PauseType == ParagraphItemType.PartPause).ToList();
            Assert.Single(partPauses);
            Assert.Equal(c1.Id, partPauses[0].ChapterId);

            Assert.Empty(plan.Where(p => p.ChapterId == c2.Id && p.PauseType == ParagraphItemType.PartPause));
        }

        // ── Test 5: two volumes → VolumePause on last chapter of first volume ─

        [Fact]
        public void PlanPauseInsertions_BetweenVolumes_InsertsVolumePauseOnLastChapterOfPrecedingVolume()
        {
            var v1 = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var v2 = new Volume { Id = Guid.NewGuid(), Order = Key(v1.Order) };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = v1.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = v2.Id, Order = Key() };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = p2.Id, Order = Key() };
            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key() };
            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c2.Id, Order = Key() };

            var h = new BookHierarchy
            {
                Volumes = [v1, v2],
                Parts = { [v1.Id] = [p1], [v2.Id] = [p2] },
                Chapters = { [p1.Id] = [c1], [p2.Id] = [c2] },
                Paragraphs = { [c1.Id] = [pg1], [c2.Id] = [pg2] },
                Items =
                {
                    [pg1.Id] = [NarrationItem(pg1.Id, Key())],
                    [pg2.Id] = [NarrationItem(pg2.Id, Key())],
                },
            };

            var plan = h.PlanPauseInsertions();

            var volPauses = plan.Where(p => p.PauseType == ParagraphItemType.VolumePause).ToList();
            Assert.Single(volPauses);
            Assert.Equal(c1.Id, volPauses[0].ChapterId);

            Assert.Empty(plan.Where(p => p.ChapterId == c2.Id && p.PauseType == ParagraphItemType.VolumePause));
        }

        // ── Test 6: highest boundary wins (worked example from plan) ─────────

        [Fact]
        public void PlanPauseInsertions_HighestBoundaryWins()
        {
            // V1[ P1[ C1, C2 ], P2[ C3 ] ], V2[ P3[ C4 ] ]
            var v1 = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var v2 = new Volume { Id = Guid.NewGuid(), Order = Key(v1.Order) };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = v1.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = v1.Id, Order = Key(p1.Order) };
            var p3 = new Part { Id = Guid.NewGuid(), VolumeId = v2.Id, Order = Key() };
            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key(c1.Order) };
            var c3 = new Chapter { Id = Guid.NewGuid(), PartId = p2.Id, Order = Key() };
            var c4 = new Chapter { Id = Guid.NewGuid(), PartId = p3.Id, Order = Key() };

            var pg1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key() };
            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c2.Id, Order = Key() };
            var pg3 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c3.Id, Order = Key() };
            var pg4 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c4.Id, Order = Key() };

            var h = new BookHierarchy
            {
                Volumes = [v1, v2],
                Parts = { [v1.Id] = [p1, p2], [v2.Id] = [p3] },
                Chapters = { [p1.Id] = [c1, c2], [p2.Id] = [c3], [p3.Id] = [c4] },
                Paragraphs = { [c1.Id] = [pg1], [c2.Id] = [pg2], [c3.Id] = [pg3], [c4.Id] = [pg4] },
                Items =
                {
                    [pg1.Id] = [NarrationItem(pg1.Id, Key())],
                    [pg2.Id] = [NarrationItem(pg2.Id, Key())],
                    [pg3.Id] = [NarrationItem(pg3.Id, Key())],
                    [pg4.Id] = [NarrationItem(pg4.Id, Key())],
                },
            };

            var plan = h.PlanPauseInsertions();

            // C1 → ChapterPause (not last chapter of P1)
            Assert.Single(plan, p => p.ChapterId == c1.Id && p.PauseType == ParagraphItemType.ChapterPause);
            // C2 → PartPause (last chapter of P1, P1 not last part of V1)
            Assert.Single(plan, p => p.ChapterId == c2.Id && p.PauseType == ParagraphItemType.PartPause);
            // C3 → VolumePause (last chapter of last part of V1, V1 not last volume)
            Assert.Single(plan, p => p.ChapterId == c3.Id && p.PauseType == ParagraphItemType.VolumePause);
            // C4 → nothing (last chapter of last part of last volume)
            Assert.Empty(plan.Where(p => p.ChapterId == c4.Id && p.PauseType != ParagraphItemType.ParagraphPause));

            // No duplicate boundary types on the same chapter
            Assert.Empty(plan.Where(p => p.ChapterId == c1.Id && p.PauseType == ParagraphItemType.PartPause));
            Assert.Empty(plan.Where(p => p.ChapterId == c1.Id && p.PauseType == ParagraphItemType.VolumePause));
            Assert.Empty(plan.Where(p => p.ChapterId == c2.Id && p.PauseType == ParagraphItemType.ChapterPause));
            Assert.Empty(plan.Where(p => p.ChapterId == c2.Id && p.PauseType == ParagraphItemType.VolumePause));
            Assert.Empty(plan.Where(p => p.ChapterId == c3.Id && p.PauseType == ParagraphItemType.ChapterPause));
            Assert.Empty(plan.Where(p => p.ChapterId == c3.Id && p.PauseType == ParagraphItemType.PartPause));
        }

        // ── Test 7: skip when pause already present ───────────────────────────

        [Fact]
        public void PlanPauseInsertions_SkipsWhenPauseAlreadyPresent()
        {
            var vol = new Volume { Id = Guid.NewGuid(), Order = Key() };
            var p1 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var p2 = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key(p1.Order) };

            var c1 = new Chapter { Id = Guid.NewGuid(), PartId = p1.Id, Order = Key() };
            var c2 = new Chapter { Id = Guid.NewGuid(), PartId = p2.Id, Order = Key() };

            // C1 has two content paras with a ParagraphPause already between them,
            // and already ends with a PartPause.
            var pA = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key() };
            var pPause = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key(pA.Order) };
            var pB = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key(pPause.Order) };
            var pBoundary = new Paragraph { Id = Guid.NewGuid(), ChapterId = c1.Id, Order = Key(pB.Order) };

            var pg2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = c2.Id, Order = Key() };

            var h = new BookHierarchy
            {
                Volumes = [vol],
                Parts = { [vol.Id] = [p1, p2] },
                Chapters = { [p1.Id] = [c1], [p2.Id] = [c2] },
                Paragraphs = { [c1.Id] = [pA, pPause, pB, pBoundary], [c2.Id] = [pg2] },
                Items =
                {
                    [pA.Id] = [NarrationItem(pA.Id, Key())],
                    [pPause.Id] = [PauseItem(pPause.Id, ParagraphItemType.ParagraphPause, Key())],
                    [pB.Id] = [NarrationItem(pB.Id, Key())],
                    [pBoundary.Id] = [PauseItem(pBoundary.Id, ParagraphItemType.PartPause, Key())],
                    [pg2.Id] = [NarrationItem(pg2.Id, Key())],
                },
            };

            var plan = h.PlanPauseInsertions();

            // ParagraphPause between pA and pB already present — must not re-emit.
            Assert.Empty(plan.Where(p =>
                p.PauseType == ParagraphItemType.ParagraphPause &&
                p.AfterOrder == pA.Order && p.BeforeOrder == pB.Order));

            // PartPause at end of c1 already present — must not re-emit.
            Assert.Empty(plan.Where(p =>
                p.ChapterId == c1.Id && p.PauseType == ParagraphItemType.PartPause));
        }
    }
}
