using System;
using System.Linq;
using System.Threading.Tasks;
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

        /// <summary>Seeds a chapter with N character-type paragraphs, each with a single item containing the given texts.</summary>
        private async Task<(Guid ChapterId, Guid[] ParagraphIds)> SeedChapterAsync(params string[] paragraphTexts)
        {
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);

            var ids = new Guid[paragraphTexts.Length];
            string? prevOrder = null;
            for (int i = 0; i < paragraphTexts.Length; i++)
            {
                var paraOrder = Key(prevOrder);
                prevOrder = paraOrder;
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = paraOrder };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    Text = paragraphTexts[i],
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                ids[i] = para.Id;
            }
            await db.SaveChangesAsync();
            return (ch.Id, ids);
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
            // Query = ids[3] ("Q"), before=4 → should get "P1","P2","P3"
            var ctx = await _reader.GetParagraphContextAsync(_folder, chId, ids[3], 4, 0);
            Assert.NotNull(ctx);
            Assert.Equal(["P1", "P2", "P3"], ctx.Preceding.Select(p => p.Text).ToArray());
            Assert.Empty(ctx.Following);
        }

        [Fact]
        public async Task GetParagraphContext_ClampsAtChapterStart()
        {
            // Only 2 paragraphs before query, but before=10
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
            // Seed: P1, [pause], P2(query), [pause], P3
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            db.Volumes.Add(vol); db.Parts.Add(part); db.Chapters.Add(ch);

            Guid AddPara(string? text, ParagraphItemType type, ref string? prev)
            {
                var order = Key(prev); prev = order;
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = order };
                var item = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para.Id, ItemType = type, Text = text, Order = Key() };
                db.Paragraphs.Add(para); db.ParagraphItems.Add(item);
                return para.Id;
            }

            string? prevKey = null;
            AddPara("P1", ParagraphItemType.Character, ref prevKey);
            AddPara(null, ParagraphItemType.ParagraphPause, ref prevKey);
            var queryId = AddPara("P2", ParagraphItemType.Character, ref prevKey);
            AddPara(null, ParagraphItemType.ChapterPause, ref prevKey);
            AddPara("P3", ParagraphItemType.Narration, ref prevKey);
            await db.SaveChangesAsync();

            var ctx = await _reader.GetParagraphContextAsync(_folder, ch.Id, queryId, 4, 2);
            Assert.NotNull(ctx);
            Assert.Equal("P2", ctx.Query.Text);
            Assert.Equal(["P1"], ctx.Preceding.Select(p => p.Text).ToArray());
            Assert.Equal(["P3"], ctx.Following.Select(p => p.Text).ToArray());
        }

        // Seeds a chapter with explicit ParagraphItem configurations for audio-selection tests.
        private async Task<(Guid ChapterId, Guid NarrationNoWavId, Guid CharAttributedNoWavId, Guid CharUnattributedId, Guid CharWithWavId, Guid NarrationWithWavId, Guid PauseId)>
            SeedAudioItemsAsync()
        {
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);

            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Characters.Add(character);

            var paraOrder = Key();
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = paraOrder };
            db.Paragraphs.Add(para);

            // Narration, no WAV — should be included when needsAudioOnly
            var narrationNoWav = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Narration, Order = Key(), AudioFileName = null,
            };
            // Character with CharacterId, no WAV — should be included when needsAudioOnly
            var charAttributedNoWav = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character, CharacterId = character.Id, Order = Key(), AudioFileName = null,
            };
            // Character without CharacterId (unattributed) — excluded when needsAudioOnly
            var charUnattributed = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character, CharacterId = null, Order = Key(), AudioFileName = null,
            };
            // Character with CharacterId AND WAV — excluded when needsAudioOnly (already has audio)
            var charWithWav = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character, CharacterId = character.Id, Order = Key(), AudioFileName = "audio/item.wav",
            };
            // Narration with WAV — excluded when needsAudioOnly
            var narrationWithWav = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.Narration, Order = Key(), AudioFileName = "audio/narr.wav",
            };
            // Pause — always excluded regardless of needsAudioOnly
            var pause = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para.Id,
                ItemType = ParagraphItemType.ParagraphPause, Order = Key(),
            };

            db.ParagraphItems.AddRange(narrationNoWav, charAttributedNoWav, charUnattributed, charWithWav, narrationWithWav, pause);
            await db.SaveChangesAsync();

            return (ch.Id, narrationNoWav.Id, charAttributedNoWav.Id, charUnattributed.Id, charWithWav.Id, narrationWithWav.Id, pause.Id);
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
        public async Task GetAudioItemRefsAsync_DefaultFlag_ReturnsFull_NonPauseSet()
        {
            var (chId, narrationNoWavId, charAttributedNoWavId, charUnattributedId, charWithWavId, narrationWithWavId, pauseId) = await SeedAudioItemsAsync();
            var refs = await _reader.GetAudioItemRefsAsync(_folder, BookNodeLevel.Chapter, chId, needsAudioOnly: false);
            var ids = refs.Select(r => r.ParagraphItemId).ToHashSet();

            // All non-Pause items included
            Assert.Contains(narrationNoWavId, ids);
            Assert.Contains(charAttributedNoWavId, ids);
            Assert.Contains(charUnattributedId, ids);
            Assert.Contains(charWithWavId, ids);
            Assert.Contains(narrationWithWavId, ids);
            // Pause excluded
            Assert.DoesNotContain(pauseId, ids);
        }

        [Fact]
        public async Task GetAudioReviews_ReturnsAllRowsForFolder()
        {
            var (_, ids) = await SeedChapterAsync("A", "B", "C");

            await using (var db = await OpenDbAsync())
            {
                // One review row per the first two character paragraphs' items.
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
