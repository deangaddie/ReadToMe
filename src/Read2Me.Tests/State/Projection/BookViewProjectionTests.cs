using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.App.State;
using Read2Me.App.State.Projection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Services.Mutations;
using Read2Me.Services.Voice;
using Read2Me.Tests.Infrastructure;
using Xunit;
using AudioReviewState = Read2Me.Data.Enums.AudioReviewState;

namespace Read2Me.Tests.State.Projection
{
    /// <summary>
    /// Behaviour of one circuit's Book View projection, exercised only through
    /// <see cref="BookViewProjection.OpenAsync"/> and the snapshots it publishes — never through the
    /// loading helpers behind them. Reads go to a real SQLite project, because "coherent" is a claim
    /// about what the persisted Book actually says.
    /// </summary>
    public class BookViewProjectionTests : ProjectDbTestBase
    {
        private const string OtherFolderName = "other-book";

        private readonly ProjectFolderId _folder;
        private readonly ProjectFolderId _otherFolder;
        private readonly ProjectReader _reader;
        private readonly BookTreeState _treeState;
        private readonly BookSelectionState _selectionState = new();
        private readonly AudioItemSelectionState _audioSelectionState = new();
        private readonly FakeVoiceResolver _voices = new();
        private readonly BookRevisionSequence _revisions = new();

        public BookViewProjectionTests()
        {
            _folder = new ProjectFolderId(FolderName);
            _otherFolder = new ProjectFolderId(OtherFolderName);

            var fileSystem = new FileSystemService(
                Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(
                fileSystem, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _treeState = new BookTreeState(new BookHierarchyLoader(_reader));
        }

        private BookViewProjection CreateSut(IBookProjectLoader? loader = null) =>
            new(loader ?? new BookProjectLoader(_reader),
                _reader,
                _treeState,
                _selectionState,
                _audioSelectionState,
                _voices,
                _revisions);

        // ── arrangement ──────────────────────────────────────────────────────

        /// <summary>A second, empty project to switch to — enough of one for its Book View to open.</summary>
        private async Task SeedOtherProjectAsync()
        {
            var path = Path.Combine(TempDir, OtherFolderName);
            Directory.CreateDirectory(path);
            await using var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(path, "project.db")};Pooling=false")
                .Options);
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project
            {
                Id = Guid.NewGuid(),
                Title = "Other book",
                BookTitle = "Other book",
                Author = "Author",
                Filename = "other.txt",
                Type = BookFileType.Text,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>One volume, one implicit part, two chapters, one narration paragraph in each.</summary>
        private async Task<BookHierarchyBuilder> SeedOneVolumeTwoChaptersAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                    .AddChapter("ch1", c => c.AddParagraph("p1", p => p.AddNarration("i1", "First chapter")))
                    .AddChapter("ch2", c => c.AddParagraph("p2", p => p.AddNarration("i2", "Second chapter"))))
                .BuildAsync();
            return b;
        }

        // ── a coherent open ──────────────────────────────────────────────────

        [Fact]
        public async Task OpenAsync_PublishesOneCoherentSnapshotOfThePersistedBook()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithProject(narratorOnlyMode: true).WithCharacter("alice", alice).WithNarratorLink(alice.Id);
            await b.AddVolume("vol", v => v.AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p.AddRawItem("i1", ParagraphItemType.Speech, "Hello", alice.Id))))
                .BuildAsync();

            var sut = CreateSut();
            var published = 0;
            sut.SnapshotPublished += () => published++;

            var snapshot = await sut.OpenAsync(_folder);

            Assert.Equal(BookViewHealth.Coherent, snapshot.Health);
            Assert.Equal(_folder, snapshot.Folder);
            Assert.Equal(_folder, sut.Folder);
            Assert.Same(snapshot, sut.Snapshot);
            Assert.Equal(1, published);

            Assert.True(snapshot.HasContent);
            Assert.Equal("book.txt", snapshot.Filename);
            Assert.Equal(b.VolumeId("vol"), Assert.Single(snapshot.Volumes).Id);
            Assert.Equal(1, snapshot.TotalParts);
            Assert.Equal(1, snapshot.TotalChapters);
            Assert.True(snapshot.NarratorOnlyMode);
            Assert.Contains(snapshot.Characters, c => c.Id == alice.Id);
            Assert.True(snapshot.Narrator.IsLinked);
            Assert.Equal(alice.Id, snapshot.Narrator.CharacterId);
            Assert.Contains(b.ChapterId("ch1"), snapshot.SelectableNodeIds);
            Assert.Equal(1, snapshot.NodeCharacterParagraphCounts[b.ChapterId("ch1")]);
            Assert.Equal(1, snapshot.AudioNodeCounts[b.ChapterId("ch1")]);
            Assert.Equal(b.ParagraphId("p1"), Assert.Single(snapshot.NodeStatus).ParagraphId);
            Assert.Equal(BookViewMode.Combined, snapshot.ViewMode);
            Assert.Null(snapshot.PlayingAudioItemId);
        }

        [Fact]
        public async Task OpenAsync_EmptyProject_PublishesASnapshotWithNoContent()
        {
            await new BookHierarchyBuilder(OpenDbAsync).BuildAsync();

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.False(snapshot.HasContent);
            Assert.Empty(snapshot.Volumes);
            Assert.Empty(snapshot.Branches.PartsByVolume);
            Assert.Equal(BookViewHealth.Coherent, snapshot.Health);
        }

        [Fact]
        public async Task OpenAsync_CarriesTheReviewsOfTheItemsThatNeedThem()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            await using (var db = await OpenDbAsync())
            {
                db.AudioReviews.Add(new AudioReview
                {
                    ParagraphItemId = b.ItemId("i1"),
                    State = AudioReviewState.NeedsReview,
                    Wer = 0.4f,
                });
                await db.SaveChangesAsync();
            }

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.NotNull(snapshot.ReviewOf(b.ItemId("i1")));
            Assert.Null(snapshot.ReviewOf(b.ItemId("i2")));
        }

        // ── revision ─────────────────────────────────────────────────────────

        [Fact]
        public async Task OpenAsync_UnmutatedProject_PublishesRevisionZero()
        {
            await SeedOneVolumeTwoChaptersAsync();

            Assert.Equal(0, (await CreateSut().OpenAsync(_folder)).Revision);
        }

        [Fact]
        public async Task OpenAsync_StampsTheRevisionTheProjectHasAlreadyReached()
        {
            await SeedOneVolumeTwoChaptersAsync();
            _revisions.Next(_folder);
            _revisions.Next(_folder);
            _revisions.Next(_otherFolder);   // another project's writes never move this one

            Assert.Equal(2, (await CreateSut().OpenAsync(_folder)).Revision);
        }

        // ── lazy loading and expansion intent ────────────────────────────────

        [Fact]
        public async Task OpenAsync_SingleVolume_OpensItButLeavesItsChaptersUnread()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();

            var snapshot = await CreateSut().OpenAsync(_folder);

            // The only volume and its only part are open — there is nothing to choose between —
            // so their children are loaded. No chapter is, so no paragraph is read.
            Assert.Contains(b.VolumeId("vol"), snapshot.Expansion.VolumeIds);
            Assert.Single(snapshot.Branches.PartsByVolume);
            Assert.Single(snapshot.Branches.ChaptersByPart);
            Assert.Empty(snapshot.Branches.ParagraphsByChapter);
            Assert.Empty(snapshot.Expansion.ChapterIds);
        }

        [Fact]
        public async Task OpenAsync_SeveralVolumesNoneExpanded_ReadsNoBranchAtAll()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("p1", p => p.AddNarration("i1", "One"))))
                .AddVolume("v2", v => v.AddChapter("c2", c => c.AddParagraph("p2", p => p.AddNarration("i2", "Two"))))
                .BuildAsync();

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.Equal(2, snapshot.Volumes.Count);
            Assert.Empty(snapshot.Expansion.VolumeIds);
            Assert.Empty(snapshot.Branches.PartsByVolume);
            Assert.Empty(snapshot.Branches.ChaptersByPart);
        }

        [Fact]
        public async Task OpenAsync_RestoresOnlyTheBranchesTheReaderHadOpen()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            _treeState.For(_folder).ExpandedChapterIds.Add(b.ChapterId("ch1"));

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.Equal(b.ChapterId("ch1"), Assert.Single(snapshot.Expansion.ChapterIds));
            Assert.True(snapshot.Branches.ParagraphsByChapter.ContainsKey(b.ChapterId("ch1")));
            Assert.False(snapshot.Branches.ParagraphsByChapter.ContainsKey(b.ChapterId("ch2")));
            Assert.Equal(b.ParagraphId("p1"), Assert.Single(snapshot.Branches.AllParagraphs()).Id);
        }

        [Fact]
        public async Task OpenAsync_ReopeningTheSameProject_KeepsTheBranchesOpen()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();

            _treeState.For(_folder).ExpandedChapterIds.Add(b.ChapterId("ch2"));
            await sut.OpenAsync(_folder);
            var reopened = await sut.OpenAsync(_folder);

            Assert.Equal(b.ChapterId("ch2"), Assert.Single(reopened.Expansion.ChapterIds));
            Assert.True(reopened.Branches.ParagraphsByChapter.ContainsKey(b.ChapterId("ch2")));
        }

        [Fact]
        public async Task OpenAsync_DropsExpansionIntentForNodesTheBookNoLongerContains()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var tree = _treeState.For(_folder);
            tree.ExpandedChapterIds.Add(b.ChapterId("ch1"));
            tree.ExpandedChapterIds.Add(Guid.NewGuid());   // a chapter that has since been deleted

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.Equal(b.ChapterId("ch1"), Assert.Single(snapshot.Expansion.ChapterIds));
            Assert.Single(tree.ExpandedChapterIds);
        }

        // ── voice previews ───────────────────────────────────────────────────

        [Fact]
        public async Task OpenAsync_ResolvesVoicePreviewsForTheLoadedItems()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            _treeState.For(_folder).ExpandedChapterIds.Add(b.ChapterId("ch1"));
            _voices.Names[b.ItemId("i1")] = "Narrator voice";

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.Equal("Narrator voice", snapshot.ResolvedVoiceName(b.ItemId("i1")));
            Assert.Equal([b.ItemId("i1")], _voices.Requested);
        }

        [Fact]
        public async Task OpenAsync_NothingLoaded_ResolvesNoVoicePreviews()
        {
            await SeedOneVolumeTwoChaptersAsync();

            var snapshot = await CreateSut().OpenAsync(_folder);

            Assert.Empty(snapshot.VoicePreviews);
            Assert.Empty(_voices.Requested);
        }

        // ── selections and project switching ─────────────────────────────────

        [Fact]
        public async Task OpenAsync_PublishesTheSelectionsAndTheirCountBasis()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p.AddRawItem("i1", ParagraphItemType.Speech, "Hello", alice.Id))))
                .BuildAsync();

            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var selection = _selectionState.For(_folder);
            selection.AddParagraph(b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")));

            var reopened = await sut.OpenAsync(_folder);

            Assert.Equal([b.ParagraphId("p1")], reopened.Selections.ParagraphIds);
            // The roll-up denominator comes from the same read as the content it counts.
            Assert.Equal(TriState.Checked, selection.NodeState(BookNodeLevel.Chapter, b.ChapterId("ch1")));
        }

        [Fact]
        public async Task OpenAsync_SwitchingProjects_DiscardsTheSelectionsOfTheOneLeftBehind()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            await SeedOtherProjectAsync();

            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            _selectionState.For(_folder).AddParagraph(
                b.ParagraphId("p1"), new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")));

            var switched = await sut.OpenAsync(_otherFolder);

            Assert.Equal(_otherFolder, switched.Folder);
            Assert.Empty(switched.Selections.ParagraphIds);
            Assert.Equal(0, _selectionState.For(_folder).SelectedParagraphCount);
        }

        [Fact]
        public async Task OpenAsync_OverlappingOpens_PublishTheLastOneAskedForLast()
        {
            await SeedOneVolumeTwoChaptersAsync();
            await SeedOtherProjectAsync();

            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);

            // The first open is held mid-read, so the switch that follows it starts while it is
            // still in flight — the race that could otherwise publish the older Book last.
            var held = new TaskCompletionSource();
            loader.Held = held;
            var first = sut.OpenAsync(_folder);
            var second = sut.OpenAsync(_otherFolder);
            held.SetResult();

            Assert.Equal(_folder, (await first).Folder);
            Assert.Equal(_otherFolder, (await second).Folder);
            Assert.Equal(_otherFolder, sut.Snapshot!.Folder);
            Assert.Equal(_otherFolder, sut.Folder);
        }

        // ── a failed build ───────────────────────────────────────────────────

        [Fact]
        public async Task OpenAsync_FailedBuild_KeepsTheLastCoherentSnapshotAndItsBinding()
        {
            await SeedOneVolumeTwoChaptersAsync();

            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            var coherent = await sut.OpenAsync(_folder);

            var published = 0;
            sut.SnapshotPublished += () => published++;
            loader.Failure = new InvalidOperationException("the database went away");

            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.OpenAsync(_otherFolder));

            Assert.Same(coherent, sut.Snapshot);
            Assert.Equal(BookViewHealth.Coherent, sut.Snapshot!.Health);
            Assert.Equal(_folder, sut.Folder);
            Assert.Equal(0, published);
        }

        [Fact]
        public async Task OpenAsync_FailedBuild_LeavesTheProjectItWasShowingUntouched()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            _treeState.For(_folder).ExpandedChapterIds.Add(b.ChapterId("ch1"));

            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            await sut.OpenAsync(_folder);
            _selectionState.For(_folder).AddParagraph(
                b.ParagraphId("p1"), new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")));

            loader.Failure = new InvalidOperationException("the database went away");
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.OpenAsync(_otherFolder));

            loader.Failure = null;
            var reopened = await sut.OpenAsync(_folder);

            Assert.Equal([b.ParagraphId("p1")], reopened.Selections.ParagraphIds);
            Assert.Equal(b.ChapterId("ch1"), Assert.Single(reopened.Expansion.ChapterIds));
        }

        // ── test doubles ─────────────────────────────────────────────────────

        private sealed class FakeVoiceResolver : IVoiceResolver
        {
            public Dictionary<Guid, string?> Names { get; } = new();
            public List<Guid> Requested { get; } = new();

            public Task<IReadOnlyDictionary<Guid, Guid?>> ResolveAsync(
                ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) =>
                throw new NotSupportedException("The projection resolves names, not ids.");

            public Task<IReadOnlyDictionary<Guid, string?>> ResolveNamesAsync(
                ProjectFolderId folder, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
            {
                Requested.AddRange(itemIds);
                IReadOnlyDictionary<Guid, string?> names =
                    itemIds.ToDictionary(id => id, id => Names.GetValueOrDefault(id));
                return Task.FromResult(names);
            }
        }

        /// <summary>
        /// A real loader that can be made to fail, or held mid-read — the two things a build does
        /// that the projection's behaviour hangs on and a real database will not do on cue.
        /// </summary>
        private sealed class SwitchableLoader(IBookProjectLoader inner) : IBookProjectLoader
        {
            public Exception? Failure { get; set; }

            /// <summary>Set to hold the next read until the task completes; cleared once it does.</summary>
            public TaskCompletionSource? Held { get; set; }

            public async Task<BookProjectSnapshot> LoadSnapshotAsync(
                ProjectFolderId folderId, CancellationToken ct = default)
            {
                if (Held is { } held)
                {
                    Held = null;
                    await held.Task;
                }

                if (Failure is { } failure) throw failure;

                return await inner.LoadSnapshotAsync(folderId, ct);
            }
        }
    }
}
