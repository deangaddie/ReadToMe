using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.State;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using AudioReviewState = Read2Me.Data.Enums.AudioReviewState;
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

        private async Task<(Guid volId, Guid characterId)> SeedProjectWithContentAsync(bool narratorOnlyMode = false)
        {
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithProject(narratorOnlyMode: narratorOnlyMode)
             .WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p.AddRawItem("item", ParagraphItemType.Speech, "Hello world", character.Id))))
                .BuildAsync();

            return (b.VolumeId("vol"), character.Id);
        }

        // ---------------------------------------------------------------
        // Happy path
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_WithContent_ReturnsCorrectSnapshot()
        {
            var (volId, characterId) = await SeedProjectWithContentAsync();

            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.Equal("book.txt", snapshot.Filename);
            Assert.True(snapshot.HasContent);
            Assert.Single(snapshot.Volumes);
            Assert.Equal(volId, snapshot.Volumes[0].Id);
            Assert.Contains(snapshot.Characters, c => c.Id == characterId);
            Assert.False(snapshot.NarratorOnlyMode);
        }

        // ---------------------------------------------------------------
        // AudioNodeCounts empty when no content
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadSnapshotAsync_NoContent_AudioNodeCountsEmpty()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

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
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph("para", p => p.AddNarration("item", "Narrated text"))))
                .BuildAsync();

            await using var db = await OpenDbAsync();
            db.AudioReviews.Add(new AudioReview
            {
                ParagraphItemId = b.ItemId("item"),
                State = AudioReviewState.NeedsReview,
                Wer = 0.1f
            });
            await db.SaveChangesAsync();

            var snapshot = await _sut.LoadSnapshotAsync(_folder);

            Assert.Single(snapshot.AudioReviews);
            Assert.Equal(b.ItemId("item"), snapshot.AudioReviews[0].ParagraphItemId);
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
