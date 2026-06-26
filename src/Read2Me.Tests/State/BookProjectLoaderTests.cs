using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.State;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.State
{
    public class BookProjectLoaderTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly IBookProjectLoader _sut;

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        public BookProjectLoaderTests()
        {
            _folder = new ProjectFolderId(FolderName);

            var dbProvider = new Read2Me.Services.ProjectDbContextProvider();
            var fileSystem = new Read2Me.Services.IO.FileSystemService(
                Microsoft.Extensions.Options.Options.Create(
                    new WorkspaceOptions { FolderPath = TempDir }));
            var dbSession = new ProjectDbSession(fileSystem, dbProvider, NullLogger<ProjectDbSession>.Instance);
            var reader = new ProjectReader(dbSession, NullLogger<ProjectReader>.Instance);
            _sut = new BookProjectLoader(reader);
        }

        private async Task<(Volume vol, Character character)> SeedProjectWithContentAsync(bool narratorOnlyMode = false)
        {
            await using var db = await OpenDbAsync();

            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Characters.Add(character);

            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol1", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key(), CharacterId = character.Id };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = para.Id,
                ItemType = ParagraphItemType.Character,
                CharacterId = character.Id,
                Text = "Hello world",
                Order = Key()
            };

            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);

            db.Projects.Add(new Project
            {
                Filename = "book.epub",
                NarratorOnlyMode = narratorOnlyMode
            });

            await db.SaveChangesAsync();
            return (vol, character);
        }

        // ---------------------------------------------------------------
        // Happy path
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_WithContent_ReturnsCorrectSnapshot()
        {
            var (vol, character) = await SeedProjectWithContentAsync();

            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.Equal("book.epub", snapshot.Filename);
            Assert.True(snapshot.HasContent);
            Assert.Single(snapshot.Volumes);
            Assert.Equal(vol.Id, snapshot.Volumes[0].Id);
            Assert.Contains(snapshot.Characters, c => c.Id == character.Id);
            Assert.False(snapshot.NarratorOnlyMode);
        }

        // ---------------------------------------------------------------
        // AudioNodeCounts empty when no content
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_NoContent_AudioNodeCountsEmpty()
        {
            // No volumes/items seeded — HasContent = false
            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.False(snapshot.HasContent);
            Assert.Empty(snapshot.AudioNodeCounts);
        }

        // ---------------------------------------------------------------
        // NarratorOnlyMode
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_NarratorOnlyModeTrue_ReflectedInSnapshot()
        {
            await SeedProjectWithContentAsync(narratorOnlyMode: true);

            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.True(snapshot.NarratorOnlyMode);
        }

        // ---------------------------------------------------------------
        // AudioReviews
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_WithAudioReview_SnapshotContainsIt()
        {
            await using var db = await OpenDbAsync();

            var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = Key() };
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = Key() };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = para.Id,
                ItemType = ParagraphItemType.Narration,
                Text = "Narrated text",
                Order = Key()
            };
            var review = new Read2Me.Data.Entities.AudioReview
            {
                ParagraphItemId = item.Id,
                State = Read2Me.Data.Enums.AudioReviewState.NeedsReview,
                Wer = 0.1f
            };

            db.Volumes.Add(vol);
            db.Parts.Add(part);
            db.Chapters.Add(ch);
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
            db.AudioReviews.Add(review);
            db.Projects.Add(new Project { Filename = "b.epub" });
            await db.SaveChangesAsync();

            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.Single(snapshot.AudioReviews);
            Assert.Equal(item.Id, snapshot.AudioReviews[0].ParagraphItemId);
        }

        // ---------------------------------------------------------------
        // NodeStatusSeed
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_WithContent_NodeStatusSeedPopulated()
        {
            await SeedProjectWithContentAsync();

            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.NotEmpty(snapshot.NodeStatusSeed);
        }
    }
}
