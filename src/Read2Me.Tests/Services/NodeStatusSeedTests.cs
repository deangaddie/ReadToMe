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
    public class NodeStatusSeedTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public NodeStatusSeedTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, null);

        private async Task<(Guid VolumeId, Guid PartId, Guid ChapterId)> SeedSpineAsync()
        {
            await using var db = await OpenDbAsync();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            await db.SaveChangesAsync();
            return (vol.Id, part.Id, ch.Id);
        }

        [Fact]
        public async Task GetNodeStatusSeed_UnattributedCharacterItem_CountsOne()
        {
            var (volId, partId, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    CharacterId = null, // unattributed
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(1, rows[0].Unattributed);
            Assert.Equal(chId, rows[0].ChapterId);
            Assert.Equal(partId, rows[0].PartId);
            Assert.Equal(volId, rows[0].VolumeId);
            Assert.Equal(1, rows[0].MissingAudio);
            Assert.Equal(0, rows[0].Review);
        }

        [Fact]
        public async Task GetNodeStatusSeed_AttributedCharacterItem_ZeroUnattributed()
        {
            var (_, _, chId) = await SeedSpineAsync();
            var characterId = Guid.NewGuid();

            await using (var db = await OpenDbAsync())
            {
                var character = new Character { Id = characterId, Name = "Alice", IsNarrator = false };
                db.Characters.Add(character);

                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    CharacterId = characterId, // attributed
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(0, rows[0].Unattributed);
        }

        [Fact]
        public async Task GetNodeStatusSeed_PauseItemOnly_NotIncluded()
        {
            var (_, _, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Pause,
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Empty(rows);
        }

        [Fact]
        public async Task GetNodeStatusSeed_MixedParagraphs_CorrectCounts()
        {
            var (_, _, chId) = await SeedSpineAsync();
            var characterId = Guid.NewGuid();

            await using (var db = await OpenDbAsync())
            {
                var character = new Character { Id = characterId, Name = "Bob", IsNarrator = false };
                db.Characters.Add(character);

                // Para 1: one unattributed character item
                var para1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                db.Paragraphs.Add(para1);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para1.Id,
                    ItemType = ParagraphItemType.Character, CharacterId = null, Order = Key(),
                });

                // Para 2: one attributed character item
                var para2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                db.Paragraphs.Add(para2);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para2.Id,
                    ItemType = ParagraphItemType.Character, CharacterId = characterId, Order = Key(),
                });

                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Equal(2, rows.Count);
            var unattributedRow = rows.Single(r => r.Unattributed == 1);
            var attributedRow = rows.Single(r => r.Unattributed == 0);
            Assert.Equal(chId, unattributedRow.ChapterId);
            Assert.Equal(chId, attributedRow.ChapterId);
        }

        [Fact]
        public async Task GetNodeStatusSeed_NarrationItem_IncludedWithZeroUnattributed()
        {
            var (_, _, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Narration,
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(0, rows[0].Unattributed);
        }

        // ---------------------------------------------------------------
        // MissingAudio (issue 0003)
        // ---------------------------------------------------------------

        [Fact]
        public async Task GetNodeStatusSeed_ItemWithNoAudioFile_CountsOneMissingAudio()
        {
            var (_, _, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    AudioFileName = null,
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(1, rows[0].MissingAudio);
        }

        [Fact]
        public async Task GetNodeStatusSeed_ItemWithAudioFile_ZeroMissingAudio()
        {
            var (_, _, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    AudioFileName = "audio/item.wav",
                    Order = Key(),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(item);
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(0, rows[0].MissingAudio);
        }

        [Fact]
        public async Task GetNodeStatusSeed_PauseItem_ExcludedFromMissingAudioCount()
        {
            var (_, _, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                // One Pause item + one Character item with no audio
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Pause,
                    AudioFileName = null,
                    Order = Key(),
                });
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    AudioFileName = null,
                    Order = Key(),
                });
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(1, rows[0].MissingAudio);
        }

        [Fact]
        public async Task GetNodeStatusSeed_TwoItemsOneMissingAudio_MissingAudioIsOne()
        {
            var (_, _, chId) = await SeedSpineAsync();

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    AudioFileName = "audio/has.wav",
                    Order = Key(),
                });
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Narration,
                    AudioFileName = null,
                    Order = Key(),
                });
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(1, rows[0].MissingAudio);
        }

        // ---------------------------------------------------------------
        // Review (issue 0004)
        // ---------------------------------------------------------------

        [Fact]
        public async Task GetNodeStatusSeed_NeedsReviewItem_ReviewIsOne()
        {
            var (_, _, chId) = await SeedSpineAsync();
            Guid itemId;

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                db.Paragraphs.Add(para);
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    AudioFileName = "audio/item.wav",
                    Order = Key(),
                };
                db.ParagraphItems.Add(item);
                itemId = item.Id;
                db.AudioReviews.Add(new AudioReview
                {
                    Id = Guid.NewGuid(),
                    ParagraphItemId = itemId,
                    State = Data.Enums.AudioReviewState.NeedsReview,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(1, rows[0].Review);
        }

        [Fact]
        public async Task GetNodeStatusSeed_DismissedReviewItem_ReviewIsZero()
        {
            var (_, _, chId) = await SeedSpineAsync();
            Guid itemId;

            await using (var db = await OpenDbAsync())
            {
                var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chId, Order = Key() };
                db.Paragraphs.Add(para);
                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(), ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Character,
                    AudioFileName = "audio/item.wav",
                    Order = Key(),
                };
                db.ParagraphItems.Add(item);
                itemId = item.Id;
                db.AudioReviews.Add(new AudioReview
                {
                    Id = Guid.NewGuid(),
                    ParagraphItemId = itemId,
                    State = Data.Enums.AudioReviewState.Dismissed,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var rows = await _reader.GetNodeStatusSeedAsync(_folder);

            Assert.Single(rows);
            Assert.Equal(0, rows[0].Review);
        }
    }
}
