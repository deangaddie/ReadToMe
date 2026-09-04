using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly FakeSelections _selections;
        private readonly BookRevisionSequence _revisions;
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _circuit;
        private readonly BookMutations _mutations;

        public BookViewProjectionTests()
        {
            _folder = new ProjectFolderId(FolderName);
            _otherFolder = new ProjectFolderId(OtherFolderName);

            // One circuit's scope, wired the way the app wires it: the write side and the reads it
            // reconciles share a ProjectDbSession, which is the only way eviction after a commit can
            // actually be observed by a rebuild.
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _circuit = _root.CreateAsyncScope();

            _reader = _circuit.ServiceProvider.GetRequiredService<ProjectReader>();
            _mutations = _circuit.ServiceProvider.GetRequiredService<BookMutations>();
            _revisions = _root.GetRequiredService<BookRevisionSequence>();
            _treeState = new BookTreeState();
            _selections = new FakeSelections(_selectionState, _audioSelectionState);
        }

        private BookViewProjection CreateSut(IBookProjectLoader? loader = null) =>
            new(loader ?? new BookProjectLoader(_reader),
                _reader,
                _reader,
                _reader,
                _mutations,
                _treeState,
                _selectionState,
                _audioSelectionState,
                _selections,
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
        // ── transient intents ────────────────────────────────────────────────

        [Fact]
        public async Task ApplyAsync_BeforeAnyOpen_IsRejected()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateSut().ApplyAsync(new BookViewIntent.TogglePlayback(Guid.NewGuid())));
        }

        [Fact]
        public async Task ApplyAsync_ExpandingAChapter_PublishesItsParagraphs()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var snapshot = await sut.ApplyAsync(
                new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), Expanded: true));

            Assert.Equal(b.ChapterId("ch1"), Assert.Single(snapshot.Expansion.ChapterIds));
            Assert.Equal(b.ParagraphId("p1"), Assert.Single(snapshot.Branches.AllParagraphs()).Id);
            Assert.Same(snapshot, sut.Snapshot);
        }

        [Fact]
        public async Task ApplyAsync_CollapsingAChapter_PublishesTheBookWithoutIt()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));

            var snapshot = await sut.ApplyAsync(
                new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), Expanded: false));

            Assert.Empty(snapshot.Expansion.ChapterIds);
            Assert.Empty(snapshot.Branches.ParagraphsByChapter);
        }

        [Fact]
        public async Task ApplyAsync_ExpandingWhatIsAlreadyOpen_PublishesNothing()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var published = 0;
            sut.SnapshotPublished += () => published++;

            var before = sut.Snapshot;
            var after = await sut.ApplyAsync(
                new BookViewIntent.SetNodeExpanded(BookNodeLevel.Volume, b.VolumeId("vol"), Expanded: true));

            Assert.Same(before, after);
            Assert.Equal(0, published);
        }

        [Fact]
        public async Task ApplyAsync_SwitchingViewMode_DropsBothSelectionsAndRebuilds()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            var opened = await sut.OpenAsync(_folder);

            _selectionState.For(_folder).AddParagraph(
                b.ParagraphId("p1"), new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")));
            _audioSelectionState.For(_folder).AddItem(
                new AudioItemRef(b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol")));

            var snapshot = await sut.ApplyAsync(new BookViewIntent.SetViewMode(BookViewMode.SplitAudio));

            Assert.Equal(BookViewMode.SplitAudio, snapshot.ViewMode);
            Assert.Empty(snapshot.Selections.ParagraphIds);
            Assert.Empty(snapshot.Selections.AudioItemIds);
            // A rebuild, not a patch: the content was read again, so the previews were too.
            Assert.NotSame(opened.Branches, snapshot.Branches);
        }

        [Fact]
        public async Task ApplyAsync_TheViewModeAlreadyShowing_PublishesNothing()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            var opened = await sut.OpenAsync(_folder);

            var after = await sut.ApplyAsync(new BookViewIntent.SetViewMode(BookViewMode.Combined));

            Assert.Same(opened, after);
        }

        [Fact]
        public async Task ApplyAsync_TogglingPlayback_PublishesTheItemWithoutRereadingTheBook()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            var opened = await sut.OpenAsync(_folder);

            var playing = await sut.ApplyAsync(new BookViewIntent.TogglePlayback(b.ItemId("i1")));

            Assert.Equal(b.ItemId("i1"), playing.PlayingAudioItemId);
            // Nothing about the Book changed, so the same reads stay published.
            Assert.Same(opened.Branches, playing.Branches);
            Assert.Equal(opened.Revision, playing.Revision);

            var stopped = await sut.ApplyAsync(new BookViewIntent.TogglePlayback(b.ItemId("i1")));
            Assert.Null(stopped.PlayingAudioItemId);
        }

        [Fact]
        public async Task ApplyAsync_SelectingAParagraph_PublishesItAndItsRollUp()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p.AddRawItem("i1", ParagraphItemType.Speech, "Hello", alice.Id))))
                .BuildAsync();

            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var snapshot = await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));

            Assert.Equal([b.ParagraphId("p1")], snapshot.Selections.ParagraphIds);
            // The roll-up counts against the denominator the same snapshot published.
            Assert.Equal(TriState.Checked,
                _selectionState.For(_folder).NodeState(BookNodeLevel.Chapter, b.ChapterId("ch1")));
        }

        [Fact]
        public async Task ApplyAsync_SelectingANodesParagraphs_CarriesTheUnattributedOnlyNarrowing()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            _selections.NodeParagraphs =
                [new CharacterParagraphRef(b.ParagraphId("p1"), b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol"))];

            var snapshot = await sut.ApplyAsync(new BookViewIntent.SetNodeParagraphsSelected(
                BookNodeLevel.Chapter, b.ChapterId("ch1"), Selected: true, UnattributedOnly: true));

            Assert.Equal([b.ParagraphId("p1")], snapshot.Selections.ParagraphIds);
            Assert.True(_selections.LastUnattributedOnly);
        }

        [Fact]
        public async Task ApplyAsync_SelectingANodesAudioItems_CarriesTheSnapshotsNarratorOnlyMode()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithProject(narratorOnlyMode: true);
            await b.AddVolume("vol", v => v.AddChapter("ch1", c => c
                    .AddParagraph("p1", p => p.AddNarration("i1", "Hello"))))
                .BuildAsync();

            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            _selections.NodeAudioItems =
                [new AudioItemRef(b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol"))];

            var snapshot = await sut.ApplyAsync(new BookViewIntent.SetNodeAudioItemsSelected(
                BookNodeLevel.Chapter, b.ChapterId("ch1"), Selected: true, NeedsAudioOnly: true));

            Assert.Equal([b.ItemId("i1")], snapshot.Selections.AudioItemIds);
            Assert.True(_selections.LastNeedsAudioOnly);
            Assert.True(_selections.LastNarratorOnlyMode);
        }

        [Fact]
        public async Task ApplyAsync_SelectingOneAudioItem_PublishesIt()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var item = new AudioItemRef(
                b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol"));
            var snapshot = await sut.ApplyAsync(new BookViewIntent.SetAudioItemSelected(item, Selected: true));

            Assert.Equal([b.ItemId("i1")], snapshot.Selections.AudioItemIds);
        }

        [Fact]
        public async Task ApplyAsync_ArmingBulkAssign_PublishesIt()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            Assert.True((await sut.ApplyAsync(new BookViewIntent.SetBulkAssign(Armed: true))).Selections.BulkMode);
            Assert.False((await sut.ApplyAsync(new BookViewIntent.SetBulkAssign(Armed: false))).Selections.BulkMode);
        }

        [Fact]
        public async Task ApplyAsync_QueueingTheSelection_PublishesTheSelectionItEmptied()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));

            var snapshot = await sut.ApplyAsync(new BookViewIntent.QueueSelectedParagraphs());

            Assert.Equal([b.ParagraphId("p1")], _selections.QueuedParagraphs);
            Assert.Empty(snapshot.Selections.ParagraphIds);
        }

        [Fact]
        public async Task ApplyAsync_QueueingTheAudioSelection_PublishesTheSelectionItEmptied()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var item = new AudioItemRef(
                b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol"));
            await sut.ApplyAsync(new BookViewIntent.SetAudioItemSelected(item, Selected: true));

            var snapshot = await sut.ApplyAsync(new BookViewIntent.QueueSelectedAudioItems());

            Assert.Equal([b.ItemId("i1")], _selections.QueuedAudioItems);
            Assert.Empty(snapshot.Selections.AudioItemIds);
        }


        // ── committed mutations ──────────────────────────────────────────────

        [Fact]
        public async Task MutateAsync_Committed_ReturnsOnlyOnceTheBookViewShowsTheChange()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            var opened = await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));

            var outcome = await sut.MutateAsync(
                new InsertPauseParagraphMutation(_folder, b.ItemId("i1"), InsertPosition.After, PauseKind.ChapterPause));

            var coherent = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome);
            Assert.Same(sut.Snapshot, coherent.Snapshot);
            Assert.Equal(coherent.Receipt.Revision, coherent.Snapshot.Revision);
            Assert.True(coherent.Snapshot.Revision > opened.Revision);

            // The loaded branch was reread, not patched: the new pause Paragraph is in it.
            var paragraphs = coherent.Snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")];
            Assert.Contains(paragraphs, p => p.Id == coherent.Receipt.Effects.CreatedId);
        }

        [Fact]
        public async Task MutateAsync_Committed_RereadsTheOverviewAndOnlyTheExpandedBranches()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));

            var outcome = await sut.MutateAsync(new AddChapterTitlesMutation(_folder));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            // Both chapters gained a title paragraph, but only the open one was read back: lazy
            // loading survives a whole-project rebuild (ADR 0007).
            Assert.Equal(2, snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")].Count);
            Assert.DoesNotContain(b.ChapterId("ch2"), snapshot.Branches.ParagraphsByChapter.Keys);
            // The overview did move: the counts the roll-ups divide by came from the same read.
            Assert.Equal(2, snapshot.AudioNodeCounts[b.ChapterId("ch1")]);
        }

        [Fact]
        public async Task MutateAsync_NoChange_PublishesNothing()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.MutateAsync(new AddPausesMutation(_folder));

            var before = sut.Snapshot;
            var published = 0;
            sut.SnapshotPublished += () => published++;

            // Nothing left to insert. No revision, so no new Book to show and nothing to announce.
            Assert.IsType<BookViewMutationOutcome.NoChange>(await sut.MutateAsync(new AddPausesMutation(_folder)));
            Assert.Same(before, sut.Snapshot);
            Assert.Equal(0, published);
        }

        [Fact]
        public async Task MutateAsync_ExpectedRefusal_LeavesTheBookViewExactlyAsItWas()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            var opened = await sut.OpenAsync(_folder);
            var published = 0;
            sut.SnapshotPublished += () => published++;

            var outcome = await sut.MutateAsync(new SplitAtItemMutation(_folder, Guid.NewGuid()));

            var uncommitted = Assert.IsType<BookViewMutationOutcome.Uncommitted>(outcome);
            Assert.Equal(BookMutationRejection.NotFound, uncommitted.Reason);
            Assert.Same(opened, sut.Snapshot);
            Assert.Equal(0, published);
        }

        [Fact]
        public async Task MutateAsync_ForABookThisProjectionIsNotOpenOn_Throws()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.MutateAsync(new AddPausesMutation(_otherFolder)));
        }

        // ── split expansion continuity ───────────────────────────────────────

        /// <summary>
        /// A two-chapter Part with a second Part beside it. The sibling matters: a lone Part is not a
        /// choice, so the tree opens it unconditionally and "the source was closed" cannot arise.
        /// </summary>
        private async Task<BookHierarchyBuilder> SeedTwoChapterPartAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                    .AddPart("part", p => p
                        .AddChapter("ch1", c => c.AddParagraph("p1", g => g.AddNarration("i1", "First")))
                        .AddChapter("ch2", c => c.AddParagraph("p2", g => g.AddNarration("i2", "Second"))))
                    .AddPart("other", p => p
                        .AddChapter("ch3", c => c.AddParagraph("p3", g => g.AddNarration("i3", "Third")))))
                .BuildAsync();
            return b;
        }

        [Fact]
        public async Task MutateAsync_Split_OpensTheNewSiblingWhenTheSourceWasOpen()
        {
            var b = await SeedTwoChapterPartAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Part, b.PartId("part"), true));

            var outcome = await sut.MutateAsync(new SplitAtChapterMutation(_folder, b.ChapterId("ch2"), "Part Two"));

            var coherent = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome);
            var newPartId = coherent.Receipt.Effects.CreatedId!.Value;
            // Both halves of what the reader was looking at stay in view.
            Assert.Contains(b.PartId("part"), coherent.Snapshot.Expansion.PartIds);
            Assert.Contains(newPartId, coherent.Snapshot.Expansion.PartIds);
            Assert.Equal(b.ChapterId("ch2"), Assert.Single(coherent.Snapshot.Branches.ChaptersByPart[newPartId]).Id);
        }

        [Fact]
        public async Task MutateAsync_Split_LeavesTheNewSiblingClosedWhenTheSourceWasClosed()
        {
            var b = await SeedTwoChapterPartAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Part, b.PartId("part"), false));

            var outcome = await sut.MutateAsync(new SplitAtChapterMutation(_folder, b.ChapterId("ch2"), "Part Two"));

            var coherent = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome);
            Assert.DoesNotContain(coherent.Receipt.Effects.CreatedId!.Value, coherent.Snapshot.Expansion.PartIds);
        }

        // ── selection recomputation ──────────────────────────────────────────

        [Fact]
        public async Task MutateAsync_ChapterSplit_KeepsTheFolderSelectionAndRestampsItsAncestry()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                    .AddParagraph("p1", p => p.AddRawItem("i1", ParagraphItemType.Speech, "One", alice.Id))
                    .AddParagraph("p2", p => p.AddRawItem("i2", ParagraphItemType.Speech, "Two", alice.Id))))
                .BuildAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var selection = _selectionState.For(_folder);
            selection.AddParagraph(b.ParagraphId("p2"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch")));

            var outcome = await sut.MutateAsync(
                new SplitAtParagraphMutation(_folder, b.ParagraphId("p2"), "Chapter Two"));

            var coherent = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome);
            var newChapterId = coherent.Receipt.Effects.CreatedId!.Value;
            // Still selected — it exists and is still eligible — but under the chapter it moved to,
            // so its roll-up counts against the right denominator.
            Assert.Equal([b.ParagraphId("p2")], coherent.Snapshot.Selections.ParagraphIds);
            Assert.Equal(newChapterId, selection.GetAncestry(b.ParagraphId("p2"))!.ChapterId);
            Assert.Equal(TriState.Checked, selection.NodeState(BookNodeLevel.Chapter, newChapterId));
            Assert.Equal(TriState.Unchecked, selection.NodeState(BookNodeLevel.Chapter, b.ChapterId("ch")));
        }

        [Fact]
        public async Task MutateAsync_ChapterSplit_KeepsTheAudioItemSelectionAndRestampsItsAncestry()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                    .AddParagraph("p1", p => p.AddNarration("i1", "One"))
                    .AddParagraph("p2", p => p.AddNarration("i2", "Two"))))
                .BuildAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var audioSelection = _audioSelectionState.For(_folder);
            audioSelection.AddItem(new AudioItemRef(
                b.ItemId("i2"), b.ParagraphId("p2"), b.ChapterId("ch"), Guid.NewGuid(), b.VolumeId("vol")));

            var outcome = await sut.MutateAsync(
                new SplitAtParagraphMutation(_folder, b.ParagraphId("p2"), "Chapter Two"));

            var coherent = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome);
            var newChapterId = coherent.Receipt.Effects.CreatedId!.Value;
            Assert.Equal([b.ItemId("i2")], coherent.Snapshot.Selections.AudioItemIds);
            Assert.Equal(TriState.Checked, audioSelection.NodeState(BookNodeLevel.Chapter, newChapterId));
        }

        [Fact]
        public async Task MutateAsync_ClearsSelectedRowsThePersistedBookNoLongerHas()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            // A row selected against an older revision that the Book no longer contains — the
            // selection the reconciliation has to drop rather than carry into a bulk write.
            var gone = Guid.NewGuid();
            var selection = _selectionState.For(_folder);
            selection.AddParagraph(gone, new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")));
            var goneItem = Guid.NewGuid();
            _audioSelectionState.For(_folder).AddItem(new AudioItemRef(
                goneItem, gone, b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol")));

            var outcome = await sut.MutateAsync(
                new InsertPauseParagraphMutation(_folder, b.ItemId("i1"), InsertPosition.After, PauseKind.Pause));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            // Cleared in the same publication as the content it no longer matches.
            Assert.Empty(snapshot.Selections.ParagraphIds);
            Assert.Empty(snapshot.Selections.AudioItemIds);
        }

        [Fact]
        public async Task MutateAsync_LeavesAnUnaffectedSelectionAlone()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v
                    .AddChapter("ch1", c => c.AddParagraph("p1", p =>
                        p.AddRawItem("i1", ParagraphItemType.Speech, "One", alice.Id)))
                    .AddChapter("ch2", c => c.AddParagraph("p2", p =>
                        p.AddRawItem("i2", ParagraphItemType.Speech, "Two", alice.Id))))
                .BuildAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            _selectionState.For(_folder).AddParagraph(b.ParagraphId("p2"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch2")));

            var outcome = await sut.MutateAsync(
                new InsertPauseParagraphMutation(_folder, b.ItemId("i1"), InsertPosition.After, PauseKind.Pause));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal([b.ParagraphId("p2")], snapshot.Selections.ParagraphIds);
        }

        // ── test doubles ─────────────────────────────────────────────────────
        /// <summary>
        /// The circuit's selection writer. The real one is <c>BookSelectionCoordinator</c>, which
        /// drags both queues and their preflight in with it; what the projection needs from it is
        /// only that a selection intent reaches the selection state the snapshot then reads.
        /// </summary>
        private sealed class FakeSelections(
            BookSelectionState paragraphs, AudioItemSelectionState items) : ISelectionCoordinator
        {
            private ProjectFolderId _folder;

            /// <summary>What a node-wide paragraph selection expands to.</summary>
            public IReadOnlyList<CharacterParagraphRef> NodeParagraphs { get; set; } = [];

            /// <summary>What a node-wide audio selection expands to.</summary>
            public IReadOnlyList<AudioItemRef> NodeAudioItems { get; set; } = [];

            public bool? LastUnattributedOnly { get; private set; }
            public bool? LastNeedsAudioOnly { get; private set; }
            public bool? LastNarratorOnlyMode { get; private set; }

            public List<Guid> QueuedParagraphs { get; } = [];
            public List<Guid> QueuedAudioItems { get; } = [];

            public void SetCurrentFolder(ProjectFolderId folderId) => _folder = folderId;

            public Task ToggleParagraphAsync(
                ProjectFolderId folderId, Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, bool on)
            {
                var selection = paragraphs.For(folderId);
                if (on)
                    selection.AddParagraph(paragraphId, new ParagraphSelection(volumeId, partId, chapterId));
                else
                    selection.RemoveParagraph(paragraphId);
                return Task.CompletedTask;
            }

            public Task SetNodeAsync(
                ProjectFolderId folderId, BookNodeLevel level, Guid id, bool on, bool unprocessedOnly = false)
            {
                LastUnattributedOnly = unprocessedOnly;
                var selection = paragraphs.For(folderId);
                if (on)
                    selection.AddParagraphs(NodeParagraphs);
                else
                    selection.RemoveParagraphs(NodeParagraphs.Select(r => r.ParagraphId));
                return Task.CompletedTask;
            }

            public int SelectedParagraphCount => paragraphs.For(_folder).SelectedParagraphCount;

            public Task AddSelectionToCharacterQueueAsync()
            {
                var selection = paragraphs.For(_folder);
                QueuedParagraphs.AddRange(selection.SelectedParagraphIds());
                selection.Clear();
                return Task.CompletedTask;
            }

            public Task ToggleAudioItemAsync(AudioItemRef item, bool on)
            {
                var selection = items.For(_folder);
                if (on) selection.AddItem(item); else selection.RemoveItem(item.ParagraphItemId);
                return Task.CompletedTask;
            }

            public Task SetAudioNodeAsync(
                ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool on,
                bool needsAudioOnly = false, bool narratorOnlyMode = false)
            {
                LastNeedsAudioOnly = needsAudioOnly;
                LastNarratorOnlyMode = narratorOnlyMode;
                var selection = items.For(folderId);
                if (on)
                    selection.AddItems(NodeAudioItems);
                else
                    selection.RemoveItems(NodeAudioItems.Select(r => r.ParagraphItemId));
                return Task.CompletedTask;
            }

            public int SelectedAudioItemCount => items.For(_folder).SelectedItemCount;

            public Task AddSelectionToAudioQueueAsync()
            {
                var selection = items.For(_folder);
                QueuedAudioItems.AddRange(selection.SelectedItems().Select(i => i.ParagraphItemId));
                selection.Clear();
                return Task.CompletedTask;
            }
        }


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

        public override async ValueTask DisposeAsync()
        {
            await _circuit.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
