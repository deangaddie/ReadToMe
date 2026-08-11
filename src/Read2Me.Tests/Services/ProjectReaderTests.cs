using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectReaderTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public ProjectReaderTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        // Seeds a chapter with N character-type paragraphs, each with a single item containing the given texts.
        // Returns (chapterId, paragraphIds[]).
        private async Task<(Guid ChapterId, Guid[] ParagraphIds)> SeedChapterAsync(params string[] paragraphTexts)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            var paraNames = paragraphTexts.Select((_, i) => $"para{i}").ToArray();
            var itemNames = paragraphTexts.Select((_, i) => $"item{i}").ToArray();

            var builder = b.AddVolume("vol", v => v.AddChapter("ch", c =>
            {
                for (int i = 0; i < paragraphTexts.Length; i++)
                {
                    var pname = paraNames[i];
                    var iname = itemNames[i];
                    var text = paragraphTexts[i];
                    c.AddParagraph(pname, p => p.AddRawItem(iname, ParagraphItemType.Character, text));
                }
            }));
            await builder.BuildAsync();

            return (b.ChapterId("ch"), paraNames.Select(n => b.ParagraphId(n)).ToArray());
        }

        [Fact]
        public async Task CountUnattributedCharacterItems_CountsOnlyUnstampedCharacterItems()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync).WithCharacter("alice", alice);
            var builder = b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("p", p =>
            {
                p.AddRawItem("stamped", ParagraphItemType.Character, "\"Hi,\"", alice.Id);
                p.AddRawItem("unstamped", ParagraphItemType.Character, "\"Who's there?\"", null);
                // Narration is stamped with the narrator and never counts as unattributed.
                p.AddRawItem("narration", ParagraphItemType.Narration, "she said.");
            })));
            await builder.BuildAsync();

            var count = await _reader.CountUnattributedCharacterItemsAsync(_folder, b.ParagraphId("p"));

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task CountUnattributedCharacterItems_FullyStampedParagraph_IsZero()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync).WithCharacter("alice", alice);
            var builder = b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("p", p =>
                p.AddRawItem("stamped", ParagraphItemType.Character, "\"Hi,\"", alice.Id))));
            await builder.BuildAsync();

            Assert.Equal(0, await _reader.CountUnattributedCharacterItemsAsync(_folder, b.ParagraphId("p")));
        }

        [Fact]
        public async Task GetParagraphContext_ReturnsQueryText()
        {
            var (chId, ids) = await SeedChapterAsync("A", "B", "C");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[1], 4, 0);
            Assert.NotNull(ctx);
            Assert.Equal("B", ctx.Query.Text);
        }

        [Fact]
        public async Task GetParagraphContext_ReturnsPrecedingParagraphs_InBookOrder()
        {
            var (chId, ids) = await SeedChapterAsync("P1", "P2", "P3", "Q", "F1");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[3], 4, 0);
            Assert.NotNull(ctx);
            Assert.Equal(["P1", "P2", "P3"], ctx.Preceding.Select(p => p.Text).ToArray());
            Assert.Empty(ctx.Following);
        }

        [Fact]
        public async Task GetParagraphContext_ClampsAtChapterStart()
        {
            var (chId, ids) = await SeedChapterAsync("A", "B", "Q");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[2], 10, 0);
            Assert.NotNull(ctx);
            Assert.Equal(["A", "B"], ctx.Preceding.Select(p => p.Text).ToArray());
        }

        [Fact]
        public async Task GetParagraphContext_DefaultAfterIsZero_NoFollowing()
        {
            var (chId, ids) = await SeedChapterAsync("A", "Q", "B", "C");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[1], 4, 0);
            Assert.NotNull(ctx);
            Assert.Empty(ctx.Following);
        }

        [Fact]
        public async Task GetParagraphContext_WithFollowing_ReturnsCorrect()
        {
            var (chId, ids) = await SeedChapterAsync("A", "Q", "B", "C", "D");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[1], 0, 2);
            Assert.NotNull(ctx);
            Assert.Empty(ctx.Preceding);
            Assert.Equal(["B", "C"], ctx.Following.Select(p => p.Text).ToArray());
        }

        [Fact]
        public async Task GetParagraphContext_UnknownParagraphId_ReturnsNull()
        {
            var (chId, _) = await SeedChapterAsync("A");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, Guid.NewGuid(), 4, 0);
            Assert.Null(ctx);
        }

        [Fact]
        public async Task GetParagraphContext_FirstParagraph_HasNoPreceding()
        {
            var (chId, ids) = await SeedChapterAsync("First", "Second");
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[0], 4, 0);
            Assert.NotNull(ctx);
            Assert.Equal("First", ctx.Query.Text);
            Assert.Empty(ctx.Preceding);
        }

        [Fact]
        public async Task GetParagraphContext_ExcludesPauseParagraphsFromContext()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para_p1", p => p.AddRawItem("i_p1", ParagraphItemType.Character, "P1"))
                .AddParagraph("para_pause1", p => p.AddPause("pause1", ParagraphItemType.ParagraphPause))
                .AddParagraph("para_query", p => p.AddRawItem("i_query", ParagraphItemType.Character, "P2"))
                .AddParagraph("para_pause2", p => p.AddPause("pause2", ParagraphItemType.ChapterPause))
                .AddParagraph("para_p3", p => p.AddNarration("i_p3", "P3"))))
                .BuildAsync();

            var ctx = await _reader.GetParagraphContextAsync(_folder, b.ChapterId("ch"), b.ParagraphId("para_query"), 4, 2);
            Assert.NotNull(ctx);
            Assert.Equal("P2", ctx.Query.Text);
            Assert.Equal(["P1"], ctx.Preceding.Select(p => p.Text).ToArray());
            Assert.Equal(["P3"], ctx.Following.Select(p => p.Text).ToArray());
        }

        // ── GetParagraphBatchContextAsync ─────────────────────────────────────

        [Fact]
        public async Task GetParagraphBatchContext_ContiguousRun_IncludesAllTargetsIndexedInOrder()
        {
            var (chId, ids) = await SeedChapterAsync("A", "B", "C", "D", "E");
            var ctx = await _reader.GetParagraphBatchContextAsync(_folder, chId, [ids[1], ids[2], ids[3]], 1, 1);

            Assert.NotNull(ctx);
            Assert.Equal([ids[1], ids[2], ids[3]], ctx.IncludedIds);
            Assert.Empty(ctx.DeferredIds);
            Assert.Equal(["A", "B", "C", "D", "E"], ctx.Entries.Select(e => e.Text).ToArray());
            Assert.Equal([null, 0, 1, 2, null], ctx.Entries.Select(e => e.TargetIndex).ToArray());
        }

        [Fact]
        public async Task GetParagraphBatchContext_UnassignedCharacterParagraphGap_DefersRemainder()
        {
            var (chId, ids) = await SeedChapterAsync("A", "B", "C", "D");
            // Request B and D; unassigned character paragraph C sits between them and is not requested.
            var ctx = await _reader.GetParagraphBatchContextAsync(_folder, chId, [ids[1], ids[3]], 0, 0);

            Assert.NotNull(ctx);
            Assert.Equal([ids[1]], ctx.IncludedIds);
            Assert.Equal([ids[3]], ctx.DeferredIds);
            Assert.Equal(["B"], ctx.Entries.Select(e => e.Text).ToArray());
        }

        [Fact]
        public async Task GetParagraphBatchContext_AttributedCharacterParagraphBetween_DoesNotBreakRun()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("p0", p => p.AddRawItem("i0", ParagraphItemType.Character, "Q1"))
                .AddParagraph("p1", p => p.AddCharacterLine("i1", "Known line", speaker: "alice"))
                .AddParagraph("p2", p => p.AddRawItem("i2", ParagraphItemType.Character, "Q2"))))
                .BuildAsync();

            var ctx = await _reader.GetParagraphBatchContextAsync(
                _folder, b.ChapterId("ch"), [b.ParagraphId("p0"), b.ParagraphId("p2")], 0, 0);

            Assert.NotNull(ctx);
            Assert.Equal([b.ParagraphId("p0"), b.ParagraphId("p2")], ctx.IncludedIds);
            Assert.Empty(ctx.DeferredIds);
            Assert.Equal(["Q1", "Known line", "Q2"], ctx.Entries.Select(e => e.Text).ToArray());
            Assert.Equal([0, null, 1], ctx.Entries.Select(e => e.TargetIndex).ToArray());
            var item = Assert.Single(ctx.Entries[1].Items);
            Assert.Equal(new ContextItem(b.ItemId("i1"), "Known line", "dialog", "Alice"), item);
        }

        [Fact]
        public async Task GetParagraphBatchContext_NarrationBetween_DoesNotBreakRun()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("p0", p => p.AddRawItem("i0", ParagraphItemType.Character, "Q1"))
                .AddParagraph("p1", p => p.AddNarration("i1", "Narration"))
                .AddParagraph("p2", p => p.AddRawItem("i2", ParagraphItemType.Character, "Q2"))))
                .BuildAsync();

            var ctx = await _reader.GetParagraphBatchContextAsync(
                _folder, b.ChapterId("ch"), [b.ParagraphId("p0"), b.ParagraphId("p2")], 0, 0);

            Assert.NotNull(ctx);
            Assert.Equal([b.ParagraphId("p0"), b.ParagraphId("p2")], ctx.IncludedIds);
            Assert.Equal([0, null, 1], ctx.Entries.Select(e => e.TargetIndex).ToArray());
            var item = Assert.Single(ctx.Entries[1].Items);
            Assert.Equal(new ContextItem(b.ItemId("i1"), "Narration", "narration", "narrator"), item);
        }

        [Fact]
        public async Task GetParagraphBatchContext_PartlyAttributedParagraph_BreaksRun()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            // p1 is a multi-speaker paragraph with one stamped and one unstamped dialog item — its
            // unknown segment would poison context, so it is not eligible as context and ends the run.
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("p0", p => p.AddRawItem("i0", ParagraphItemType.Character, "Q1"))
                .AddParagraph("p1", p => p
                    .AddCharacterLine("i1a", "Known line", speaker: "alice")
                    .AddRawItem("i1b", ParagraphItemType.Character, "Unstamped line"))
                .AddParagraph("p2", p => p.AddRawItem("i2", ParagraphItemType.Character, "Q2"))))
                .BuildAsync();

            var ctx = await _reader.GetParagraphBatchContextAsync(
                _folder, b.ChapterId("ch"), [b.ParagraphId("p0"), b.ParagraphId("p2")], 0, 0);

            Assert.NotNull(ctx);
            Assert.Equal([b.ParagraphId("p0")], ctx.IncludedIds);
            Assert.Equal([b.ParagraphId("p2")], ctx.DeferredIds);
        }

        [Fact]
        public async Task GetParagraphContext_MultiItemParagraph_ReturnsRawTextAndItemsWithIds()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("p0", p => p
                    .AddCharacterLine("i0a", "\"Hello,\"", speaker: "alice")
                    .AddNarration("i0b", "she said,")
                    .AddRawItem("i0c", ParagraphItemType.Character, "\"and goodbye.\""))
                .AddParagraph("p1", p => p.AddRawItem("i1", ParagraphItemType.Character, "Q"))))
                .BuildAsync();

            var ctx = await _reader.GetParagraphContextAsync(
                _folder, b.ChapterId("ch"), b.ParagraphId("p1"), 4, 0);

            Assert.NotNull(ctx);
            var preceding = Assert.Single(ctx.Preceding);
            Assert.Equal("\"Hello,\" she said, \"and goodbye.\"", preceding.Text);
            // Items carry their own ids, in Order sequence — the handles the apply stamps by.
            Assert.Equal(
                [
                    new ContextItem(b.ItemId("i0a"), "\"Hello,\"", "dialog", "Alice"),
                    new ContextItem(b.ItemId("i0b"), "she said,", "narration", "narrator"),
                    new ContextItem(b.ItemId("i0c"), "\"and goodbye.\"", "dialog", "unknown"),
                ],
                preceding.Items);
        }

        [Fact]
        public async Task GetParagraphBatchContext_WindowsClampAtChapterEdges()
        {
            var (chId, ids) = await SeedChapterAsync("A", "B", "C");
            var ctx = await _reader.GetParagraphBatchContextAsync(_folder, chId, [ids[0], ids[1], ids[2]], 5, 5);

            Assert.NotNull(ctx);
            Assert.Equal(["A", "B", "C"], ctx.Entries.Select(e => e.Text).ToArray());
            Assert.Equal([0, 1, 2], ctx.Entries.Select(e => e.TargetIndex).ToArray());
        }

        [Fact]
        public async Task GetParagraphBatchContext_FirstParagraphUnknown_ReturnsNull()
        {
            var (chId, _) = await SeedChapterAsync("A");
            var ctx = await _reader.GetParagraphBatchContextAsync(_folder, chId, [Guid.NewGuid()], 4, 2);
            Assert.Null(ctx);
        }

        [Fact]
        public async Task GetParagraphBatchContext_SingleTarget_MatchesSingleContextShape()
        {
            var (chId, ids) = await SeedChapterAsync("A", "B", "C");
            var ctx = await _reader.GetParagraphBatchContextAsync(_folder, chId, [ids[1]], 1, 1);

            Assert.NotNull(ctx);
            Assert.Equal([ids[1]], ctx.IncludedIds);
            Assert.Equal(["A", "B", "C"], ctx.Entries.Select(e => e.Text).ToArray());
            Assert.Equal([null, 0, null], ctx.Entries.Select(e => e.TargetIndex).ToArray());
        }

        // Seeds a chapter with explicit ParagraphItem configurations for audio-selection tests.
        // Returns (chapterId, narrationNoWavId, charAttributedNoWavId, charUnattributedId, charWithWavId, narrationWithWavId, pauseId)
        private async Task<(Guid ChapterId, Guid NarrationNoWavId, Guid CharAttributedNoWavId, Guid CharUnattributedId, Guid CharWithWavId, Guid NarrationWithWavId, Guid PauseId)>
            SeedAudioItemsAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("para")))
                .BuildAsync();

            var narrationNoWavId = Guid.NewGuid();
            var charAttributedNoWavId = Guid.NewGuid();
            var charUnattributedId = Guid.NewGuid();
            var charWithWavId = Guid.NewGuid();
            var narrationWithWavId = Guid.NewGuid();
            var pauseId = Guid.NewGuid();

            await using var db = await OpenDbAsync();
            db.ParagraphItems.AddRange(
                new ParagraphItem { Id = narrationNoWavId, ParagraphId = b.ParagraphId("para"), ItemType = ParagraphItemType.Narration, Order = Key(), AudioFileName = null },
                new ParagraphItem { Id = charAttributedNoWavId, ParagraphId = b.ParagraphId("para"), ItemType = ParagraphItemType.Character, CharacterId = character.Id, Order = Key(), AudioFileName = null },
                new ParagraphItem { Id = charUnattributedId, ParagraphId = b.ParagraphId("para"), ItemType = ParagraphItemType.Character, CharacterId = null, Order = Key(), AudioFileName = null },
                new ParagraphItem { Id = charWithWavId, ParagraphId = b.ParagraphId("para"), ItemType = ParagraphItemType.Character, CharacterId = character.Id, Order = Key(), AudioFileName = "audio/item.wav" },
                new ParagraphItem { Id = narrationWithWavId, ParagraphId = b.ParagraphId("para"), ItemType = ParagraphItemType.Narration, Order = Key(), AudioFileName = "audio/narr.wav" },
                new ParagraphItem { Id = pauseId, ParagraphId = b.ParagraphId("para"), ItemType = ParagraphItemType.ParagraphPause, Order = Key() }
            );
            await db.SaveChangesAsync();

            return (b.ChapterId("ch"), narrationNoWavId, charAttributedNoWavId, charUnattributedId, charWithWavId, narrationWithWavId, pauseId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_IncludesNarrationWithNoWav()
        {
            var (chId, narrationNoWavId, _, _, _, _, _) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: true);
            Assert.Contains(refs, r => r.ParagraphItemId == narrationNoWavId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_IncludesAttributedCharacterWithNoWav()
        {
            var (chId, _, charAttributedNoWavId, _, _, _, _) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: true);
            Assert.Contains(refs, r => r.ParagraphItemId == charAttributedNoWavId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_ExcludesUnattributedCharacter()
        {
            var (chId, _, _, charUnattributedId, _, _, _) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: true);
            Assert.DoesNotContain(refs, r => r.ParagraphItemId == charUnattributedId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_ExcludesItemsWithWav()
        {
            var (chId, _, _, _, charWithWavId, narrationWithWavId, _) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: true);
            Assert.DoesNotContain(refs, r => r.ParagraphItemId == charWithWavId);
            Assert.DoesNotContain(refs, r => r.ParagraphItemId == narrationWithWavId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_ExcludesPauseItems()
        {
            var (chId, _, _, _, _, _, pauseId) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: true);
            Assert.DoesNotContain(refs, r => r.ParagraphItemId == pauseId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_NarratorOnlyMode_IncludesUnattributedCharacter()
        {
            var (chId, _, _, charUnattributedId, _, _, _) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(
                _folder, BookNodeLevel.Chapter, chId,
                needsAudioOnly: true, narratorOnlyMode: true);
            Assert.Contains(refs, r => r.ParagraphItemId == charUnattributedId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_NarratorOnlyMode_ExcludesUnattributedCharacterWithExistingWav()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("para"))).BuildAsync();

            var itemId = Guid.NewGuid();
            await using var db = await OpenDbAsync();
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = itemId, ParagraphId = b.ParagraphId("para"),
                ItemType = ParagraphItemType.Character, CharacterId = null,
                Order = Key(), AudioFileName = "audio/existing.wav"
            });
            await db.SaveChangesAsync();

            var refs = await _reader.GetAudioItemRefsAsync(
                _folder, BookNodeLevel.Chapter, b.ChapterId("ch"),
                needsAudioOnly: true, narratorOnlyMode: true);
            Assert.DoesNotContain(refs, r => r.ParagraphItemId == itemId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_NeedsAudioOnly_NarratorOnlyMode_False_ExcludesUnattributedCharacter()
        {
            var (chId, _, _, charUnattributedId, _, _, _) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(
                _folder, BookNodeLevel.Chapter, chId,
                needsAudioOnly: true, narratorOnlyMode: false);
            Assert.DoesNotContain(refs, r => r.ParagraphItemId == charUnattributedId);
        }

        [Fact]
        public async Task GetAudioItemRefsAsync_DefaultFlag_ReturnsFull_NonPauseSet()
        {
            var (chId, narrationNoWavId, charAttributedNoWavId, charUnattributedId, charWithWavId, narrationWithWavId, pauseId) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: false);
            var ids = refs.Select(r => r.ParagraphItemId).ToHashSet();

            Assert.Contains(narrationNoWavId, ids);
            Assert.Contains(charAttributedNoWavId, ids);
            Assert.Contains(charUnattributedId, ids);
            Assert.Contains(charWithWavId, ids);
            Assert.Contains(narrationWithWavId, ids);
            Assert.DoesNotContain(pauseId, ids);
        }

        [Fact]
        public async Task GetAudioReviews_ReturnsAllRowsForFolder()
        {
            var (_, ids) = await SeedChapterAsync("A", "B", "C");

            await using (var db = await OpenDbAsync())
            {
                var items = db.ParagraphItems
                    .Where(i => i.ParagraphId == ids[0] || i.ParagraphId == ids[1])
                    .Select(i => i.Id)
                    .ToList();
                db.AudioReviews.Add(new AudioReview
                {
                    Id = Guid.NewGuid(), ParagraphItemId = items[0],
                    State = Read2Me.Data.Enums.AudioReviewState.NeedsReview,
                    NormalizeOk = true, VerifyOk = false, Wer = 0.3, VerifyReason = "over",
                });
                db.AudioReviews.Add(new AudioReview
                {
                    Id = Guid.NewGuid(), ParagraphItemId = items[1],
                    State = Read2Me.Data.Enums.AudioReviewState.Dismissed,
                    NormalizeOk = false, NormalizeReason = "clip", VerifyOk = true,
                });
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetAudioReviewsAsync(_folder);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Info.State == Read2Me.Core.Models.AudioReviewState.NeedsReview && r.Info.Wer == 0.3);
            Assert.Contains(rows, r => r.Info.State == Read2Me.Core.Models.AudioReviewState.Dismissed && r.Info.NormalizeReason == "clip");
        }
    }
}
