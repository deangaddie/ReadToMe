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
using Read2Me.Services.Commands;
using Read2Me.Services.Events;
using Read2Me.Services.IO;
using Read2Me.Services.Mutations;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
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
        private readonly EventBroadcaster<BookMutationReceipt> _receipts;
        private readonly ProjectDbSession _session;
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
            _receipts = _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>();
            _session = _circuit.ServiceProvider.GetRequiredService<ProjectDbSession>();
            _treeState = new BookTreeState();
            _selections = new FakeSelections(_selectionState, _audioSelectionState);
        }

        private BookViewProjection CreateSut(
            IBookProjectLoader? loader = null,
            IBookContentReader? content = null,
            IVoiceResolver? voices = null) =>
            new(loader ?? new BookProjectLoader(_reader),
                content ?? _reader,
                _reader,
                _reader,
                _mutations,
                _treeState,
                _selectionState,
                _audioSelectionState,
                _selections,
                voices ?? _voices,
                _revisions,
                _session,
                _receipts,
                NullLogger<BookViewProjection>.Instance);

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

        // ── merge and deletion continuity ────────────────────────────────────

        [Fact]
        public async Task MutateAsync_Merge_MovesExpansionFromTheDeletedNodeOntoItsSurvivor()
        {
            var b = await SeedTwoChapterPartAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Part, b.PartId("part"), true));
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch2"), true));

            var outcome = await sut.MutateAsync(
                new MergeChapterMutation(_folder, b.ChapterId("ch2"), MergeDirection.Previous));

            var coherent = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome);
            // The surviving content does not collapse: what was open on the node that went away is
            // open on the one that took its place, and the deleted id is gone from the intent.
            Assert.Contains(b.ChapterId("ch1"), coherent.Snapshot.Expansion.ChapterIds);
            Assert.DoesNotContain(b.ChapterId("ch2"), coherent.Snapshot.Expansion.ChapterIds);
            Assert.Equal(2, coherent.Snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")].Count);
        }

        [Fact]
        public async Task MutateAsync_Delete_DropsTheDeletedBranchFromExpansionAndTheOverview()
        {
            var b = await SeedTwoChapterPartAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Part, b.PartId("part"), true));
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch2"), true));

            var outcome = await sut.MutateAsync(new DeleteChapterMutation(_folder, b.ChapterId("ch2")));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.DoesNotContain(b.ChapterId("ch2"), snapshot.Expansion.ChapterIds);
            Assert.DoesNotContain(b.ChapterId("ch2"), snapshot.Branches.ParagraphsByChapter.Keys);
            Assert.DoesNotContain(snapshot.Branches.ChaptersByPart[b.PartId("part")], c => c.Id == b.ChapterId("ch2"));
        }

        [Fact]
        public async Task MutateAsync_ClearBookContent_LeavesNoPreClearBranchRendered()
        {
            var b = await SeedTwoChapterPartAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));

            var outcome = await sut.MutateAsync(new ClearBookContentMutation(_folder));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.False(snapshot.HasContent);
            Assert.Empty(snapshot.Volumes);
            Assert.Empty(snapshot.Expansion.VolumeIds);
            Assert.Empty(snapshot.Expansion.ChapterIds);
            Assert.Empty(snapshot.Branches.ParagraphsByChapter);
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

        [Fact]
        public async Task MutateAsync_Deletion_ClearsTheSelectedRowsItRemoved()
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
            _audioSelectionState.For(_folder).AddItem(new AudioItemRef(
                b.ItemId("i2"), b.ParagraphId("p2"), b.ChapterId("ch2"), Guid.NewGuid(), b.VolumeId("vol")));

            var outcome = await sut.MutateAsync(new DeleteChapterMutation(_folder, b.ChapterId("ch2")));

            // Neither selection can survive content the Book no longer has, and both are dropped in
            // the publication that removes it — never one paint later.
            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Empty(snapshot.Selections.ParagraphIds);
            Assert.Empty(snapshot.Selections.AudioItemIds);
        }

        [Fact]
        public async Task MutateAsync_ClearBookContent_ClearsBothSelections()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", alice);
            await b.AddVolume("vol", v => v.AddChapter("ch", c => c.AddParagraph("p1", p =>
                    p.AddRawItem("i1", ParagraphItemType.Speech, "One", alice.Id))))
                .BuildAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            _selectionState.For(_folder).AddParagraph(b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch")));
            _audioSelectionState.For(_folder).AddItem(new AudioItemRef(
                b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch"), Guid.NewGuid(), b.VolumeId("vol")));

            var outcome = await sut.MutateAsync(new ClearBookContentMutation(_folder));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Empty(snapshot.Selections.ParagraphIds);
            Assert.Empty(snapshot.Selections.AudioItemIds);
        }

        // ── converging on other circuits ─────────────────────────────────────

        /// <summary>
        /// A second circuit writing the same Book: its own scope, its own session and its own
        /// <see cref="BookMutations"/>, sharing only the singletons the app shares — the revision
        /// sequence and the receipt broadcast.
        /// </summary>
        // ── exact speaker attribution ────────────────────────────────────────

        /// <summary>
        /// One volume, two chapters, each with a paragraph of dialog plus narration — enough for a
        /// speaker change in one chapter to be provably not a reread of the other.
        /// </summary>
        private async Task<BookHierarchyBuilder> SeedTwoAttributableChaptersAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = Guid.NewGuid(), Name = "Alice" })
                .WithCharacter("bob", new Character { Id = Guid.NewGuid(), Name = "Bob" });
            await b.AddVolume("vol", v => v
                    .AddChapter("ch1", c => c.AddParagraph("p1", p => p
                        .AddCharacterLine("i1", "\"Hello,\" ", "alice")
                        .AddNarration("n1", "she said.")))
                    .AddChapter("ch2", c => c.AddParagraph("p2", p => p
                        .AddCharacterLine("i2", "\"Bye.\"", "bob"))))
                .BuildAsync();
            return b;
        }

        private async Task<BookViewProjection> OpenWithBothChaptersAsync(
            BookHierarchyBuilder b, CountingContent content)
        {
            var sut = CreateSut(content: content);
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch2"), true));
            return sut;
        }

        [Fact]
        public async Task MutateAsync_ExactAttribution_RefreshesTheAffectedParagraphOnly()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            content.ChildrenOf.Clear();

            var outcome = await sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i1"), b.CharacterId("bob")));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            // The restamped row is on screen…
            var stamped = Assert.Single(snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]);
            Assert.Equal(b.CharacterId("bob"), stamped.Items.Single(i => i.Id == b.ItemId("i1")).CharacterId);
            Assert.Equal([b.ParagraphId("p1")], content.ParagraphsRead);
            // …and neither expanded branch was walked to get it there.
            Assert.Empty(content.ChildrenOf);
            // The other open chapter keeps the instance it had, untouched.
            Assert.Equal(b.CharacterId("bob"),
                snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch2")]
                    .Single().Items.Single().CharacterId);
        }

        [Fact]
        public async Task MutateAsync_ExactAttribution_RefreshesTheDerivedStateThatMovedWithIt()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            _voices.Names[b.ItemId("i1")] = "Bob's voice";

            // The paragraph's only dialog item becomes narration, so the paragraph stops being a
            // Character paragraph: its count, its selectable node and its Node Status row all move.
            var outcome = await sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i1"), ProjectDbContext.NarratorId));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.False(snapshot.NodeCharacterParagraphCounts.TryGetValue(b.ChapterId("ch1"), out var count) && count > 0);
            Assert.DoesNotContain(b.ChapterId("ch1"), snapshot.SelectableNodeIds);
            Assert.Equal(0, snapshot.NodeStatus.Single(r => r.ParagraphId == b.ParagraphId("p1")).Unattributed);
            // The Voice each restamped line would be spoken in was re-resolved with it.
            Assert.Equal("Bob's voice", snapshot.ResolvedVoiceName(b.ItemId("i1")));
            Assert.Contains(snapshot.Characters, c => c.Id == b.CharacterId("alice"));
        }

        [Fact]
        public async Task MutateAsync_ExactAttribution_ClearsASelectionThatStoppedBeingAttributable()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));
            Assert.Single(sut.Snapshot!.Selections.ParagraphIds);

            // Its last dialog item becomes narration (ADR-0006), so the Paragraph leaves the
            // Folder Selection with the denominator it was rolled up into.
            var outcome = await sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i1"), ProjectDbContext.NarratorId));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Empty(snapshot.Selections.ParagraphIds);
        }

        [Fact]
        public async Task MutateAsync_ExactAttribution_KeepsASelectionThatIsStillAttributable()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));

            // A bulk assign that swaps one character for another moves no denominator, so the dock
            // bar stays up and a second bulk gesture still has something to act on.
            var outcome = await sut.MutateAsync(
                new SetParagraphsSpeakerMutation(_folder, [b.ParagraphId("p1")], b.CharacterId("bob")));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal([b.ParagraphId("p1")], snapshot.Selections.ParagraphIds);
        }

        [Fact]
        public async Task AnotherProducersExactAttribution_ConvergesSilentlyAndWithoutRebuilding()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            content.ChildrenOf.Clear();

            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update =>
            {
                updates++;
                if (update.Announce) announced++;
            };

            // The Character Queue, working through the Book in its own scope.
            var committed = await RemoteWriter().CommitAsync(new AttributeParagraphItemsMutation(
                _folder, b.ParagraphId("p2"), [new ItemAttribution(b.ItemId("i2"), b.CharacterId("alice"), null)]));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the queue's attribution never arrived");

            Assert.Equal(b.CharacterId("alice"),
                sut.Snapshot!.Branches.ParagraphsByChapter[b.ChapterId("ch2")]
                    .Single().Items.Single().CharacterId);
            Assert.Equal([b.ParagraphId("p2")], content.ParagraphsRead);
            Assert.Empty(content.ChildrenOf);
            // Routine background progress: nothing structural, nothing lost.
            Assert.Equal(1, updates);
            Assert.Equal(0, announced);
        }

        // — Voices and Voice Rules -------------------------------------------
        //
        // These use the real VoiceResolver rather than the fake, because the claim under test is that
        // the preview names the Voice the Audio Queue would actually resolve. A stubbed answer would
        // prove only that the projection reread something.

        /// <summary>Opens the Book with both chapters expanded and previews resolved for real.</summary>
        private async Task<BookViewProjection> OpenWithRealPreviewsAsync(BookHierarchyBuilder b)
        {
            var sut = CreateSut(voices: new VoiceResolver(_session));
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch2"), true));
            return sut;
        }

        /// <summary>Gives Alice two Voices; the first becomes the one her default rule names.</summary>
        private async Task<(Guid First, Guid Second)> GiveAliceTwoVoicesAsync(BookHierarchyBuilder b)
        {
            var first = await CreateVoiceAsync(b.CharacterId("alice"), "First");
            var second = await CreateVoiceAsync(b.CharacterId("alice"), "Second");
            return (first, second);
        }

        private async Task<Guid> CreateVoiceAsync(Guid characterId, string name)
        {
            var committed = await RemoteWriter().CommitAsync(new CreateVoiceMutation(_folder, characterId, name));
            return Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Effects.CreatedId!.Value;
        }

        /// <summary>
        /// The gesture this slice exists for: somebody repoints a Character's default Voice Rule in
        /// another circuit, and this Book View's preview must start naming the Voice the Audio Queue
        /// would now use — without anyone navigating away and back.
        /// </summary>
        [Fact]
        public async Task AnotherCircuitsVoiceRuleChange_RerereadsTheVoicePreviews()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (_, second) = await GiveAliceTwoVoicesAsync(b);
            var sut = await OpenWithRealPreviewsAsync(b);

            Assert.Equal("First", sut.Snapshot!.ResolvedVoiceName(b.ItemId("i1")));

            var announced = 0;
            sut.ExternalUpdateApplied += update => { if (update.Announce) announced++; };

            var committed = await RemoteWriter().CommitAsync(new SetVoiceDefaultMutation(_folder, second));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the Voice Rule change never arrived");

            Assert.Equal("Second", sut.Snapshot!.ResolvedVoiceName(b.ItemId("i1")));
            // Nothing structural, nothing selected was lost: neither half of the announce rule fires.
            Assert.Equal(0, announced);
        }

        /// <summary>
        /// A Voice rename moves no rule and no line, and still changes what every item spoken in that
        /// Voice is labelled — which is why the Voices facet is not one a reader can place on a single
        /// Paragraph.
        /// </summary>
        [Fact]
        public async Task MutateAsync_VoiceRename_RepublishesThePreviewsUnderTheNewName()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (first, _) = await GiveAliceTwoVoicesAsync(b);
            var sut = await OpenWithRealPreviewsAsync(b);

            var outcome = await sut.MutateAsync(new UpdateVoiceMutation(_folder, first, "Young Alice", null));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal("Young Alice", snapshot.ResolvedVoiceName(b.ItemId("i1")));
        }

        // — audio assignment and reviews -------------------------------------

        private static AudioReviewVerdict CleanTake() =>
            new(NormalizeOk: true, NormalizeReason: null,
                VerifyOk: true, Wer: 0.0, VerifyReason: null,
                Transcript: "said", OriginalTextSnapshot: "said");

        private static AudioReviewVerdict FailedTake() =>
            new(NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.42, VerifyReason: "over threshold",
                Transcript: "heard", OriginalTextSnapshot: "said");

        [Fact]
        public async Task MutateAsync_ExactAudioRecording_RefreshesTheAffectedParagraphOnly()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            content.ChildrenOf.Clear();

            var outcome = await sut.MutateAsync(new RecordParagraphItemAudioMutation(
                _folder, b.ItemId("i1"), "audio/i1.wav", CleanTake()));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            var refreshed = Assert.Single(snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]);
            Assert.Equal("audio/i1.wav", refreshed.Items.Single(i => i.Id == b.ItemId("i1")).AudioFileName);
            Assert.Equal([b.ParagraphId("p1")], content.ParagraphsRead);
            // The queue writes one of these per item; walking both open chapters for each is the
            // cost this family exists to avoid.
            Assert.Empty(content.ChildrenOf);
        }

        /// <summary>
        /// The two indicators a take moves are read from the same snapshot: a row cannot come back
        /// playing new audio while its review chip still describes the take before it.
        /// </summary>
        [Fact]
        public async Task MutateAsync_AFailedTake_MovesTheAudioAndReviewBadgesTogether()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = await OpenWithBothChaptersAsync(b, new CountingContent(_reader));

            var outcome = await sut.MutateAsync(new RecordParagraphItemAudioMutation(
                _folder, b.ItemId("i1"), "audio/i1.wav", FailedTake()));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            var status = snapshot.NodeStatus.Single(r => r.ParagraphId == b.ParagraphId("p1"));
            Assert.Equal(1, status.MissingAudio);   // the paragraph's narration still has none
            Assert.Equal(1, status.Review);
            Assert.Equal(Read2Me.Core.Models.AudioReviewState.NeedsReview, snapshot.ReviewOf(b.ItemId("i1"))!.State);
        }

        [Fact]
        public async Task MutateAsync_DismissingAReview_ClearsTheBadgeAndTheDetailTogether()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            await sut.MutateAsync(new RecordParagraphItemAudioMutation(
                _folder, b.ItemId("i1"), "audio/i1.wav", FailedTake()));
            content.ChildrenOf.Clear();

            var outcome = await sut.MutateAsync(new DismissAudioReviewMutation(_folder, b.ItemId("i1")));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal(0, snapshot.NodeStatus.Single(r => r.ParagraphId == b.ParagraphId("p1")).Review);
            Assert.Equal(Read2Me.Core.Models.AudioReviewState.Dismissed, snapshot.ReviewOf(b.ItemId("i1"))!.State);
            Assert.Empty(content.ChildrenOf);
        }

        /// <summary>
        /// An item that has just been given audio is still a legal audio target — regenerating it is
        /// the whole point of selecting one that already has a take — so the selection stands, with
        /// the roll-up basis recomputed against the new revision.
        /// </summary>
        [Fact]
        public async Task MutateAsync_AudioRecording_KeepsTheAudioItemSelectionItLeftEligible()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetAudioItemSelected(
                new AudioItemRef(b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch1"), b.PartId("vol"), b.VolumeId("vol")),
                Selected: true));

            var outcome = await sut.MutateAsync(new RecordParagraphItemAudioMutation(
                _folder, b.ItemId("i1"), "audio/i1.wav", CleanTake()));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal([b.ItemId("i1")], snapshot.Selections.AudioItemIds);
        }

        /// <summary>
        /// A take recorded against one item still rechecks the whole Audio Item Selection: a row
        /// selected against an older revision that the Book no longer contains is dropped in the
        /// same publication that shows the take.
        /// </summary>
        [Fact]
        public async Task MutateAsync_AudioRecording_ClearsAnAudioSelectionThePersistedBookNoLongerHas()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var gone = Guid.NewGuid();
            await sut.ApplyAsync(new BookViewIntent.SetAudioItemSelected(
                new AudioItemRef(gone, Guid.NewGuid(), b.ChapterId("ch2"), b.PartId("vol"), b.VolumeId("vol")),
                Selected: true));

            var outcome = await sut.MutateAsync(new RecordParagraphItemAudioMutation(
                _folder, b.ItemId("i1"), "audio/i1.wav", CleanTake()));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Empty(snapshot.Selections.AudioItemIds);
        }

        /// <summary>
        /// The Audio Queue, working through the Book from its own scope: badges and review details
        /// reach this Book View without navigation, without rereading its expanded branches, and
        /// without a notice — routine progress is not a surprise (ADR 0007).
        /// </summary>
        [Fact]
        public async Task AnotherProducersRecordedTake_ConvergesSilentlyAndWithoutRebuilding()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            content.ChildrenOf.Clear();

            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update =>
            {
                updates++;
                if (update.Announce) announced++;
            };

            var committed = await RemoteWriter().CommitAsync(new RecordParagraphItemAudioMutation(
                _folder, b.ItemId("i2"), "audio/i2.wav", FailedTake()));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the queue's recorded take never arrived");

            Assert.Equal("audio/i2.wav",
                sut.Snapshot!.Branches.ParagraphsByChapter[b.ChapterId("ch2")]
                    .Single().Items.Single().AudioFileName);
            Assert.Equal(Read2Me.Core.Models.AudioReviewState.NeedsReview, sut.Snapshot!.ReviewOf(b.ItemId("i2"))!.State);
            Assert.Equal([b.ParagraphId("p2")], content.ParagraphsRead);
            Assert.Empty(content.ChildrenOf);
            Assert.Equal(1, updates);
            Assert.Equal(0, announced);
        }

        private BookMutations RemoteWriter()
        {
            var circuit = _root.CreateAsyncScope();
            _otherCircuits.Add(circuit);
            return circuit.ServiceProvider.GetRequiredService<BookMutations>();
        }

        private readonly List<AsyncServiceScope> _otherCircuits = [];

        /// <summary>
        /// Waits for an asynchronous convergence, which by design nobody is awaiting. The timeout is
        /// generous because it is only ever hit by a genuine failure to converge.
        /// </summary>
        private static async Task ConvergesAsync(Func<bool> condition, string expectation)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(10);
            }

            Assert.Fail($"The Book View never converged: {expectation}");
        }

        /// <summary>
        /// One receipt from a producer that is not a Book View — a queue reporting progress. Written
        /// by hand because the families that report these effects have not migrated yet, and because
        /// what is under test is how a projection treats a receipt, not who produced it.
        /// </summary>
        private BookMutationReceipt ExternalReceipt(BookFacets facets, ProjectFolderId? folder = null) =>
            new(folder ?? _folder, "SomeoneElsesMutation", Guid.NewGuid(), _revisions.Next(folder ?? _folder),
                new BookMutationEffects { Scope = BookMutationScope.Exact, Facets = facets });

        [Fact]
        public async Task AnotherCircuitsCommit_ConvergesThisBookViewWithoutBeingAsked()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));
            Assert.Single(sut.Snapshot!.Branches.ParagraphsByChapter[b.ChapterId("ch1")]);

            var committed = await RemoteWriter().CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("i1"), InsertPosition.After, "Inserted elsewhere"));

            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;
            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision,
                "another circuit's insertion never reached it");
            // The expanded branch was reread, not merely invalidated: the new item is on screen.
            var paragraph = Assert.Single(sut.Snapshot!.Branches.ParagraphsByChapter[b.ChapterId("ch1")]);
            Assert.Equal(2, paragraph.Items.Count);
        }

        /// <summary>
        /// An API client's command is a producer like any other (ADR 0007): it commits through the
        /// same module, publishes the same receipt, and so invalidates <em>every</em> Book View open
        /// on that project — including one whose reader is doing nothing at all. Before the
        /// migration an API-originated write reached no open circuit, which is the drift this proves
        /// is gone.
        /// </summary>
        [Fact]
        public async Task ACommandFromTheApi_ConvergesEveryOpenBookView()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var first = CreateSut();
            var second = CreateOtherCircuitSut();
            await first.OpenAsync(_folder);
            await second.OpenAsync(_folder);
            await first.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));
            var announced = 0;
            first.ExternalUpdateApplied += update => { if (update.Announce) announced++; };

            var response = await ApiCommands().ExecuteAsync(
                new InsertParagraphItemCommand(_folder, b.ItemId("i1"), InsertPosition.After, "From an agent"),
                CancellationToken.None);

            var created = response.EntityId;
            Assert.NotNull(created);
            await ConvergesAsync(
                () => first.Snapshot!.Branches.ParagraphsByChapter.TryGetValue(b.ChapterId("ch1"), out var loaded)
                      && loaded.Count == 1 && loaded[0].Items.Any(i => i.Id == created),
                "the API's insertion never reached the first Book View");
            await ConvergesAsync(
                () => second.Snapshot!.Revision >= first.Snapshot!.Revision,
                "the API's insertion never reached the second Book View");
            // Structural, and neither circuit asked for it, so both readers are told.
            Assert.Equal(1, announced);
        }

        /// <summary>
        /// What an API request runs, in a scope of its own — no Book View lives in it. The
        /// endpoint's own JSON mapping over this is <c>BookCommandApiAdapter</c>'s, and is tested
        /// where it lives.
        /// </summary>
        private BookCommandDispatcher ApiCommands()
        {
            var request = _root.CreateAsyncScope();
            _otherCircuits.Add(request);
            return request.ServiceProvider.GetRequiredService<BookCommandDispatcher>();
        }

        /// <summary>
        /// A second reader's Book View, with the scope and transient state a second circuit really
        /// has — <see cref="CreateSut"/> shares this one's, which is fine while only one projection
        /// is live but not when two must converge independently.
        /// </summary>
        private BookViewProjection CreateOtherCircuitSut()
        {
            var circuit = _root.CreateAsyncScope();
            _otherCircuits.Add(circuit);
            var reader = circuit.ServiceProvider.GetRequiredService<ProjectReader>();
            var paragraphs = new BookSelectionState();
            var items = new AudioItemSelectionState();
            return new BookViewProjection(
                new BookProjectLoader(reader), reader, reader, reader,
                circuit.ServiceProvider.GetRequiredService<BookMutations>(),
                new BookTreeState(), paragraphs, items, new FakeSelections(paragraphs, items),
                new FakeVoiceResolver(), _revisions,
                circuit.ServiceProvider.GetRequiredService<ProjectDbSession>(),
                _receipts, NullLogger<BookViewProjection>.Instance);
        }

        [Fact]
        public async Task AnotherCircuitsStructuralChange_IsAnnouncedOnce()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update =>
            {
                updates++;
                if (update.Announce) announced++;
            };

            await RemoteWriter().CommitAsync(new SplitAtParagraphMutation(_folder, b.ParagraphId("p2"), "New"));

            await ConvergesAsync(() => updates == 1, "the structural change never arrived");
            Assert.Equal(1, announced);
        }

        /// <summary>
        /// The reread case the import slice exists for: a second Book View open on the same project
        /// while someone else replaces its content. It must land on the new Book — never on the empty
        /// one the replacement passes through internally — with the selection that pointed at the old
        /// rows dropped, and it must be told, because both halves of the announce rule apply.
        /// </summary>
        [Fact]
        public async Task AnotherCircuitsReread_ReplacesThisBookViewWithoutEverShowingAnEmptyBook()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            _selectionState.For(_folder).AddParagraph(
                b.ParagraphId("p1"), new ParagraphSelection(b.VolumeId("vol"), b.PartId("vol"), b.ChapterId("ch1")));

            var emptyBooksSeen = 0;
            var announced = 0;
            sut.SnapshotPublished += () => { if (!sut.Snapshot!.HasContent) emptyBooksSeen++; };
            sut.ExternalUpdateApplied += update => { if (update.Announce) announced++; };

            var content = new BookContent([new VolumeContent(
                "Reread", [new PartContent(null, [new ChapterContent("Fresh", [new ParagraphContent("Brand new.")])])])]);
            var committed = await RemoteWriter().CommitAsync(
                new ImportBookContentMutation(_folder, content, ReplaceExisting: true));

            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;
            await ConvergesAsync(() => sut.Snapshot!.Revision >= revision, "the reread never reached it");

            Assert.Equal("Reread", Assert.Single(sut.Snapshot!.Volumes).Title);
            Assert.Equal(0, emptyBooksSeen);
            Assert.Empty(sut.Snapshot!.Selections.ParagraphIds);
            Assert.Equal(1, announced);
        }

        [Fact]
        public async Task AnotherProducersAttributionProgress_ConvergesSilently()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update =>
            {
                updates++;
                if (update.Announce) announced++;
            };

            _receipts.Publish(ExternalReceipt(BookFacets.Attribution | BookFacets.Characters));

            await ConvergesAsync(() => updates == 1, "the attribution progress never arrived");
            Assert.Equal(0, announced);
        }

        [Fact]
        public async Task AnExternalDeletionThatCostsTheReaderTheirSelection_IsAnnounced()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));
            Assert.Single(sut.Snapshot!.Selections.ParagraphIds);

            var announced = 0;
            sut.ExternalUpdateApplied += update => { if (update.Announce) announced++; };

            await RemoteWriter().CommitAsync(new DeleteParagraphMutation(_folder, b.ParagraphId("p1")));

            await ConvergesAsync(
                () => sut.Snapshot!.Selections.ParagraphIds.Count == 0,
                "the selection on the deleted Paragraph was never cleared");
            Assert.Equal(1, announced);
        }

        [Fact]
        public async Task ThisCircuitsOwnMutation_IsNeitherAnnouncedNorReconciledTwice()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var updates = 0;
            sut.ExternalUpdateApplied += _ => updates++;

            await sut.MutateAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("i1"), InsertPosition.After, "Mine"));
            var published = 0;
            sut.SnapshotPublished += () => published++;

            // A later external change is the barrier: the pump is serial, so once this has converged
            // the projection has demonstrably had every earlier receipt in its hands.
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution));

            await ConvergesAsync(() => updates == 1, "the later external change never arrived");
            Assert.Equal(1, published);
        }

        [Fact]
        public async Task AReceiptOlderThanTheSnapshot_RepublishesNothing()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var stale = ExternalReceipt(BookFacets.Structure);

            var sut = CreateSut();
            // Opens at the revision the receipt already produced, so its Book View is not behind it.
            await sut.OpenAsync(_folder);
            var published = 0;
            var updates = 0;
            sut.SnapshotPublished += () => published++;
            sut.ExternalUpdateApplied += _ => updates++;

            _receipts.Publish(stale);
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution));

            await ConvergesAsync(() => updates == 1, "the newer receipt never arrived");
            Assert.Equal(1, published);
        }

        [Fact]
        public async Task ABurstOfReceipts_CoalescesIntoOneRebuild()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            await sut.OpenAsync(_folder);

            var published = 0;
            sut.SnapshotPublished += () => published++;

            // Hold the reconciliation of the first receipt open, so the rest of the burst has to
            // queue behind it rather than being reconciled one at a time.
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.Held = held;
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution));
            await ConvergesAsync(() => loader.Reading, "the first receipt never started a rebuild");

            long newest = 0;
            for (var i = 0; i < 5; i++)
                newest = ExternalReceiptPublished(BookFacets.Attribution);

            held.SetResult();

            await ConvergesAsync(() => sut.Snapshot!.Revision >= newest, "the burst never converged");
            Assert.Equal(2, published);
            Assert.Equal(_folder, sut.Snapshot!.Folder);
            Assert.Equal(2, sut.Snapshot.TotalChapters);
            Assert.Equal(b.VolumeId("vol"), Assert.Single(sut.Snapshot.Volumes).Id);
        }

        [Fact]
        public async Task MoreReceiptsThanTheMailboxHolds_StillConvergeOnTheNewestRevision()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            await sut.OpenAsync(_folder);

            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.Held = held;
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution));
            await ConvergesAsync(() => loader.Reading, "the first receipt never started a rebuild");

            // Well past the bound, so the batch collapses to a whole-project marker. What is dropped
            // is the detail; the change itself still has to arrive.
            long newest = 0;
            for (var i = 0; i < BookViewReceiptMailbox.Capacity + 10; i++)
                newest = ExternalReceiptPublished(BookFacets.Attribution);

            // A real structural change made while the mailbox is over its bound: the detail naming it
            // is gone by the time the pump looks, and the Book View still has to end up showing it.
            var committed = await RemoteWriter().CommitAsync(new SplitAtParagraphMutation(_folder, b.ParagraphId("p2"), "New"));
            newest = Math.Max(newest, Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision);

            held.SetResult();

            await ConvergesAsync(() => sut.Snapshot!.Revision >= newest, "the overflowing burst never converged");
            Assert.Equal(3, sut.Snapshot!.TotalChapters);
        }

        [Fact]
        public async Task AnOverflowingBurstOfQueueProgress_IsStillNotAnnounced()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            await sut.OpenAsync(_folder);

            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update =>
            {
                updates++;
                if (update.Announce) announced++;
            };

            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.Held = held;
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution));
            await ConvergesAsync(() => loader.Reading, "the first receipt never started a rebuild");

            // Nothing structural anywhere in the burst — only a queue attributing speakers. Losing the
            // detail to the bound must not turn that into a change the reader is interrupted for.
            for (var i = 0; i < BookViewReceiptMailbox.Capacity + 10; i++)
                ExternalReceiptPublished(BookFacets.Attribution | BookFacets.Audio);

            held.SetResult();

            await ConvergesAsync(() => updates == 2, "the overflowing burst never converged");
            Assert.Equal(0, announced);
        }

        [Fact]
        public async Task AProjectionThatFailsToConverge_KeepsItsSnapshotAndConvergesOnTheNext()
        {
            await SeedOneVolumeTwoChaptersAsync();
            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            var opened = await sut.OpenAsync(_folder);

            loader.Failure = new InvalidOperationException("The read failed while converging.");
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution));
            await ConvergesAsync(() => loader.Failures == 1, "the failing convergence never ran");

            // The last coherent snapshot's content is still the one on screen: a failed convergence
            // publishes nothing rather than half of something, and only says it went stale.
            Assert.Same(opened.Branches, sut.Snapshot!.Branches);
            Assert.Equal(opened.Revision, sut.Snapshot!.Revision);
            Assert.Equal(BookViewHealth.Stale, sut.Snapshot!.Health);

            loader.Failure = null;
            var revision = ExternalReceiptPublished(BookFacets.Attribution);

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision,
                "the pump did not survive a failed convergence");
            Assert.Equal(BookViewHealth.Coherent, sut.Snapshot!.Health);
        }

        private long ExternalReceiptPublished(BookFacets facets)
        {
            var receipt = ExternalReceipt(facets);
            _receipts.Publish(receipt);
            return receipt.Revision;
        }

        [Fact]
        public async Task AfterSwitchingProjects_ReceiptsForThePreviousBookAreIgnored()
        {
            await SeedOneVolumeTwoChaptersAsync();
            await SeedOtherProjectAsync();

            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.OpenAsync(_otherFolder);

            var published = 0;
            var updates = 0;
            sut.SnapshotPublished += () => published++;
            sut.ExternalUpdateApplied += _ => updates++;

            _receipts.Publish(ExternalReceipt(BookFacets.Structure));
            _receipts.Publish(ExternalReceipt(BookFacets.Attribution, _otherFolder));

            await ConvergesAsync(() => updates == 1, "the receipt for the bound Book never arrived");
            Assert.Equal(1, published);
            Assert.Equal(_otherFolder, sut.Snapshot!.Folder);
        }

        [Fact]
        public async Task ADisposedProjection_StopsConverging()
        {
            var b = await SeedOneVolumeTwoChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var published = 0;
            sut.SnapshotPublished += () => published++;

            sut.Dispose();
            await RemoteWriter().CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("i1"), InsertPosition.After, "One"));

            // A second projection converging on the same Book is the barrier: it proves receipts are
            // being broadcast and reconciled by someone, and that this one chose not to.
            var live = CreateSut();
            await live.OpenAsync(_folder);
            var committed = await RemoteWriter().CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("i2"), InsertPosition.After, "Two"));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;
            await ConvergesAsync(() => live.Snapshot!.Revision >= revision, "the live projection never converged");

            Assert.Equal(0, published);
        }

        // ── manual and AI book edits ─────────────────────────────────────────
        //
        // The two halves of the edit family reconcile differently on purpose. Item text names the
        // Paragraph it rewrote and moves only data that lives on it, so it refreshes rows; a node
        // title lives in the loaded hierarchy branch instead, where a targeted refresh cannot reach
        // it, so it rebuilds.

        /// <summary>An item with a generated take and a failed verdict on it, in the persisted Book.</summary>
        private async Task RecordATakeAsync(Guid itemId) =>
            Assert.IsType<BookMutationOutcome.Committed>(
                await RemoteWriter().CommitAsync(
                    new RecordParagraphItemAudioMutation(_folder, itemId, "audio/take.wav", FailedTake())));

        [Fact]
        public async Task MutateAsync_ItemTextEdit_RefreshesTheAffectedParagraphOnly()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var content = new CountingContent(_reader);
            var sut = await OpenWithBothChaptersAsync(b, content);
            content.ChildrenOf.Clear();

            var outcome = await sut.MutateAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("n1"), "she whispered."));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            var rewritten = Assert.Single(snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]);
            Assert.Equal("she whispered.", rewritten.Items.Single(i => i.Id == b.ItemId("n1")).Text);
            Assert.Equal([b.ParagraphId("p1")], content.ParagraphsRead);
            Assert.Empty(content.ChildrenOf);
        }

        /// <summary>
        /// The whole point of the rewrite clearing the take: the row has to come back Generatable,
        /// with no verdict on audio that no longer exists and with the chapter's audio-remaining
        /// count moved to match — all in the one snapshot, so the two can never disagree.
        /// </summary>
        [Fact]
        public async Task MutateAsync_ItemTextEdit_MovesTheAudioReviewAndCountsWithTheText()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            await RecordATakeAsync(b.ItemId("n1"));
            var sut = await OpenWithBothChaptersAsync(b, new CountingContent(_reader));
            Assert.NotNull(sut.Snapshot!.ReviewOf(b.ItemId("n1")));

            var outcome = await sut.MutateAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("n1"), "she whispered."));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            var rewritten = snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]
                .Single().Items.Single(i => i.Id == b.ItemId("n1"));
            Assert.Null(rewritten.AudioFileName);
            Assert.Null(snapshot.ReviewOf(b.ItemId("n1")));
            var status = snapshot.NodeStatus.Single(r => r.ParagraphId == b.ParagraphId("p1"));
            Assert.Equal(0, status.Review);
            Assert.Equal(2, status.MissingAudio);
        }

        /// <summary>
        /// A rewrite hands the item back to the Audio Queue, so an Audio Item Selection holding it is
        /// still pointing at a present, eligible row — the recheck must keep it rather than clear
        /// everything an edit touched.
        /// </summary>
        [Fact]
        public async Task MutateAsync_ItemTextEdit_KeepsTheAudioItemSelectionItLeftEligible()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetAudioItemSelected(
                new AudioItemRef(b.ItemId("n1"), b.ParagraphId("p1"), b.ChapterId("ch1"), b.PartId("vol"), b.VolumeId("vol")),
                Selected: true));

            var outcome = await sut.MutateAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("n1"), "she whispered."));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal([b.ItemId("n1")], snapshot.Selections.AudioItemIds);
        }

        /// <summary>
        /// A title is not on a Paragraph, so the row-refresh path cannot put it on screen. Reporting
        /// it as its own facet is what sends the reconciliation down the rebuild path that rereads the
        /// loaded branch it lives in.
        /// </summary>
        [Fact]
        public async Task MutateAsync_ChapterTitleEdit_RepublishesTheTitleInTheLoadedBranch()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = await OpenWithBothChaptersAsync(b, new CountingContent(_reader));

            var outcome = await sut.MutateAsync(
                new UpdateChapterTitleMutation(_folder, b.ChapterId("ch1"), "The Meeting"));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal("The Meeting", LoadedChapter(snapshot, b.ChapterId("ch1")).Title);
        }

        [Fact]
        public async Task MutateAsync_AnEditThatRewritesNothing_PublishesNothing()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = await OpenWithBothChaptersAsync(b, new CountingContent(_reader));
            var before = sut.Snapshot!;

            var outcome = await sut.MutateAsync(
                new UpdateParagraphItemTextMutation(_folder, b.ItemId("n1"), "she said."));

            Assert.IsType<BookViewMutationOutcome.NoChange>(outcome);
            Assert.Same(before, sut.Snapshot);
        }

        /// <summary>
        /// The gesture this slice exists for: somebody accepts an AI edit program in another circuit,
        /// and this Book View shows every part of it — the retitled chapter and the rewritten item —
        /// without anyone navigating away and back. It arrives as one convergence because it committed
        /// as one mutation, and silently, because no node moved and no selection was lost.
        /// </summary>
        [Fact]
        public async Task AnotherCircuitsAcceptedAiEdit_ReachesThisBookViewInOneGo()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = await OpenWithBothChaptersAsync(b, new CountingContent(_reader));

            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update =>
            {
                updates++;
                if (update.Announce) announced++;
            };

            var committed = await RemoteWriter().CommitAsync(new ApplyBookEditsMutation(_folder,
            [
                new BookEditItem(BookEditTargetKind.ChapterTitle, b.ChapterId("ch1"), "The Meeting"),
                new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("n1"), "she whispered."),
            ]));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the accepted AI edit never arrived");

            var snapshot = sut.Snapshot!;
            Assert.Equal("The Meeting", LoadedChapter(snapshot, b.ChapterId("ch1")).Title);
            Assert.Equal("she whispered.", snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]
                .Single().Items.Single(i => i.Id == b.ItemId("n1")).Text);
            Assert.Equal(1, updates);
            Assert.Equal(0, announced);
        }

        /// <summary>One chapter as the Book View has it loaded, whichever part it hangs under.</summary>
        private static Chapter LoadedChapter(BookViewSnapshot snapshot, Guid chapterId) =>
            snapshot.Branches.ChaptersByPart.Values
                .SelectMany(chapters => chapters)
                .Single(c => c.Id == chapterId);

        // ── characters, narrator and policy ──────────────────────────────────

        [Fact]
        public async Task MutateAsync_Rename_RepublishesTheRosterEverythingLabelsRowsFrom()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var outcome = await sut.MutateAsync(
                new RenameCharacterMutation(_folder, b.CharacterId("alice"), "Alicia"));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal("Alicia", snapshot.Characters.Single(c => c.Id == b.CharacterId("alice")).Name);
        }

        [Fact]
        public async Task MutateAsync_NarratorLink_RepublishesWhoNarratesTheBook()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            Assert.False(sut.Snapshot!.Narrator.IsLinked);

            var outcome = await sut.MutateAsync(
                new SetNarratorCharacterMutation(_folder, b.CharacterId("alice")));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal(b.CharacterId("alice"), snapshot.Narrator.CharacterId);
            Assert.Equal("Alice", snapshot.Narrator.DisplayName);
        }

        [Fact]
        public async Task MutateAsync_NarratorOnlyMode_RepublishesThePolicyDerivedStateHangsOff()
        {
            await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            Assert.False(sut.Snapshot!.NarratorOnlyMode);

            var outcome = await sut.MutateAsync(new SetNarratorOnlyModeMutation(_folder, true));

            Assert.True(Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot.NarratorOnlyMode);
        }

        /// <summary>
        /// A delete hands the merged character's lines back to the queue, so the rows on screen must
        /// stop naming her — and the Folder Selection has to be recomputed rather than assumed, even
        /// though this particular paragraph survives it: unattributed dialog is still attributable.
        /// </summary>
        [Fact]
        public async Task MutateAsync_CharacterDelete_ClearsTheSpeakersOnScreenAndRechecksTheSelection()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));
            await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));

            var outcome = await sut.MutateAsync(new DeleteCharacterMutation(_folder, b.CharacterId("alice")));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.DoesNotContain(snapshot.Characters, c => c.Id == b.CharacterId("alice"));
            Assert.Null(snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]
                .Single().Items.Single(i => i.Id == b.ItemId("i1")).CharacterId);
            Assert.Equal([b.ParagraphId("p1")], snapshot.Selections.ParagraphIds);
        }

        [Fact]
        public async Task MutateAsync_CharacterMerge_MovesTheLinesOnScreenToTheSurvivor()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));

            var outcome = await sut.MutateAsync(new MergeCharactersMutation(
                _folder, b.CharacterId("bob"), b.CharacterId("alice"), AddNameAsAlias: false));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.DoesNotContain(snapshot.Characters, c => c.Id == b.CharacterId("alice"));
            Assert.Equal(b.CharacterId("bob"), snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]
                .Single().Items.Single(i => i.Id == b.ItemId("i1")).CharacterId);
        }

        /// <summary>
        /// The reader is on the Characters tab of a second circuit while this Book View is open. A
        /// rename there has to reach the labels here without anyone navigating — and quietly, because
        /// a roster change is neither structural nor a lost selection.
        /// </summary>
        [Fact]
        public async Task AnotherCircuitsRename_ReachesThisBookViewSilently()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var announced = 0;
            sut.ExternalUpdateApplied += update => { if (update.Announce) announced++; };

            var committed = await RemoteWriter().CommitAsync(
                new RenameCharacterMutation(_folder, b.CharacterId("alice"), "Alicia"));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the rename never reached this Book View");

            Assert.Equal("Alicia", sut.Snapshot!.Characters.Single(c => c.Id == b.CharacterId("alice")).Name);
            Assert.Equal(0, announced);
        }

        /// <summary>
        /// Narrator-only mode makes <em>more</em> of the Book speakable, not less — everything is read
        /// in the narrator's voice, so an item with no speaker stops being an obstacle. A selected
        /// item therefore survives the flip, and this pins that: it is the one policy in the family
        /// that could plausibly cost a reader their Audio Item Selection, and it does not.
        /// </summary>
        [Fact]
        public async Task MutateAsync_NarratorOnlyMode_KeepsTheAudioItemSelectionItLeftEligible()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetAudioItemSelected(
                new AudioItemRef(b.ItemId("i1"), b.ParagraphId("p1"), b.ChapterId("ch1"), Guid.NewGuid(), b.VolumeId("vol")),
                Selected: true));

            var outcome = await sut.MutateAsync(new SetNarratorOnlyModeMutation(_folder, true));

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.True(snapshot.NarratorOnlyMode);
            Assert.Equal([b.ItemId("i1")], snapshot.Selections.AudioItemIds);
        }

        /// <summary>
        /// Nothing in this family removes a Paragraph or an item — a delete unlinks lines rather than
        /// deleting them — so no roster gesture can cost a reader their selection, and none of them is
        /// structural either. Both halves of the announce rule are therefore false, and a roster
        /// change committed elsewhere must reach the reader without interrupting them.
        /// </summary>
        [Fact]
        public async Task AnotherCircuitsCharacterDelete_KeepsTheSelectionAndStaysSilent()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetParagraphSelected(
                b.ParagraphId("p1"),
                new ParagraphSelection(b.VolumeId("vol"), Guid.NewGuid(), b.ChapterId("ch1")), Selected: true));

            var announced = 0;
            sut.ExternalUpdateApplied += update => { if (update.Announce) announced++; };

            var committed = await RemoteWriter().CommitAsync(
                new DeleteCharacterMutation(_folder, b.CharacterId("alice")));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the delete never reached this Book View");

            Assert.DoesNotContain(sut.Snapshot!.Characters, c => c.Id == b.CharacterId("alice"));
            Assert.Equal([b.ParagraphId("p1")], sut.Snapshot!.Selections.ParagraphIds);
            Assert.Equal(0, announced);
        }

        [Fact]
        public async Task AnotherCircuitsAliasChange_ReachesThisBookView()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            var committed = await RemoteWriter().CommitAsync(
                new AddCharacterAliasMutation(_folder, b.CharacterId("alice"), "Ally"));
            var revision = Assert.IsType<BookMutationOutcome.Committed>(committed).Receipt.Revision;

            await ConvergesAsync(
                () => sut.Snapshot!.Revision >= revision, "the alias never reached this Book View");
        }

        [Fact]
        public async Task AnotherCircuitsNarratorLink_ReachesThisBookView()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            await RemoteWriter().CommitAsync(
                new SetNarratorCharacterMutation(_folder, b.CharacterId("bob")));

            await ConvergesAsync(
                () => sut.Snapshot!.Narrator.CharacterId == b.CharacterId("bob"),
                "the narrator link never reached this Book View");
        }

        // ── recovering from a failed reconciliation ──────────────────────────
        //
        // The Book View has two attempts at every committed change: the targeted refresh, and the
        // rebuild it falls back to. Both read the same Book from the same place, so a failure of
        // both is a failure to read at all — and what the reader keeps then is the last view this
        // projection could vouch for, marked stale, with the Book itself left alone (ADR 0007).

        /// <summary>A read failure for the loader to raise; nothing catches it by type.</summary>
        private static InvalidOperationException ReadFailure() => new("the database went away");

        /// <summary>
        /// Both chapters open over a loader a test can break, with the writes real: the setup every
        /// recovery case starts from, because staleness is only reachable through a commit.
        /// </summary>
        private async Task<(BookViewProjection Sut, SwitchableLoader Loader, CountingContent Content)>
            OpenOverABreakableLoaderAsync(BookHierarchyBuilder b)
        {
            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var content = new CountingContent(_reader);
            var sut = CreateSut(loader, content);
            await sut.OpenAsync(_folder);
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch1"), true));
            await sut.ApplyAsync(new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch2"), true));
            content.ChildrenOf.Clear();
            return (sut, loader, content);
        }

        /// <summary>Commits a speaker change: exact, row-scoped effects, so a targeted refresh is tried first.</summary>
        private Task<BookViewMutationOutcome> RestampAsync(BookViewProjection sut, BookHierarchyBuilder b) =>
            sut.MutateAsync(new SetItemSpeakerMutation(_folder, b.ItemId("i1"), b.CharacterId("bob")));

        /// <summary>The speaker on screen, which a stale Book View is allowed to disagree with.</summary>
        private static Guid? RenderedSpeakerOf(BookViewSnapshot snapshot, BookHierarchyBuilder b) =>
            snapshot.Branches.ParagraphsByChapter[b.ChapterId("ch1")]
                .Single().Items.Single(i => i.Id == b.ItemId("i1")).CharacterId;

        [Fact]
        public async Task MutateAsync_ATargetedRefreshThatFails_RebuildsInsteadOfGoingStale()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, content) = await OpenOverABreakableLoaderAsync(b);

            // One read fails — the targeted refresh's — and the rebuild behind it gets a live Book.
            loader.Failure = ReadFailure();
            loader.FailFor = 1;

            var outcome = await RestampAsync(sut, b);

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(outcome).Snapshot;
            Assert.Equal(BookViewHealth.Coherent, snapshot.Health);
            Assert.Equal(1, loader.Failures);
            // A rebuild, not a refresh: it walked the expanded branches to get there.
            Assert.NotEmpty(content.ChildrenOf);
            Assert.Equal(b.CharacterId("bob"), RenderedSpeakerOf(snapshot, b));
        }

        [Fact]
        public async Task MutateAsync_WhenTheRefreshAndTheRebuildBothFail_KeepsTheLastCoherentSnapshot()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            var coherent = sut.Snapshot!;

            loader.Failure = ReadFailure();
            var outcome = await RestampAsync(sut, b);

            var stale = Assert.IsType<BookViewMutationOutcome.CommittedButStale>(outcome);
            Assert.Equal(BookViewHealth.Stale, stale.Snapshot!.Health);
            Assert.Same(sut.Snapshot, stale.Snapshot);

            // Both attempts were made, and neither published a candidate: what is on screen is the
            // coherent content it already had, now saying only that it cannot be vouched for.
            Assert.Equal(2, loader.Failures);
            Assert.Same(coherent.Branches, stale.Snapshot.Branches);
            Assert.Equal(coherent.Revision, stale.Snapshot.Revision);
            Assert.Equal(coherent.Selections, stale.Snapshot.Selections);
            Assert.NotEqual(b.CharacterId("bob"), RenderedSpeakerOf(stale.Snapshot, b));
        }

        /// <summary>
        /// Committed-but-stale is a committed outcome. A caller that read it as a failure would apply
        /// the same change twice, and a producer holding a staged artifact would throw it away.
        /// </summary>
        [Fact]
        public async Task MutateAsync_CommittedButStale_ReportsAChangeTheBookActuallyKept()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);

            loader.Failure = ReadFailure();
            var outcome = await RestampAsync(sut, b);

            var stale = Assert.IsType<BookViewMutationOutcome.CommittedButStale>(outcome);
            Assert.True(outcome.Committed);
            Assert.Equal(b.ParagraphId("p1"), Assert.Single(stale.Receipt.Effects.ParagraphIds));
            Assert.Equal(b.CharacterId("bob"), await PersistedSpeakerOfAsync(b.ItemId("i1")));
        }

        [Fact]
        public async Task MutateAsync_WhileStale_IsRefusedAndChangesNothing()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            loader.Failure = ReadFailure();
            await RestampAsync(sut, b);

            // The read is healthy again, but nobody has said so: a stale Book View is not a basis to
            // compose another change on, however well the database happens to be feeling.
            loader.Failure = null;
            var refused = await sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i2"), b.CharacterId("alice")));

            var uncommitted = Assert.IsType<BookViewMutationOutcome.Uncommitted>(refused);
            Assert.Equal(BookMutationRejection.Stale, uncommitted.Reason);
            Assert.NotEqual(b.CharacterId("alice"), await PersistedSpeakerOfAsync(b.ItemId("i2")));
        }

        [Fact]
        public async Task ApplyAsync_WhileStale_StillMovesTheViewAndStaysStale()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            loader.Failure = ReadFailure();
            await RestampAsync(sut, b);

            // Safe viewing: reading, selecting and playing are not writes, so none of them is blocked.
            var played = await sut.ApplyAsync(new BookViewIntent.TogglePlayback(b.ItemId("i1")));

            Assert.Equal(b.ItemId("i1"), played.PlayingAudioItemId);
            Assert.Equal(BookViewHealth.Stale, played.Health);
        }

        /// <summary>
        /// An expansion reads the Book again, so it is tempting to treat it as a recovery. It is not:
        /// it rechecks no selection, which is exactly the half of reconciliation that failed.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_AnExpansionWhileStale_DoesNotDeclareTheViewHealthy()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            loader.Failure = ReadFailure();
            await RestampAsync(sut, b);
            loader.Failure = null;

            var collapsed = await sut.ApplyAsync(
                new BookViewIntent.SetNodeExpanded(BookNodeLevel.Chapter, b.ChapterId("ch2"), false));

            Assert.DoesNotContain(b.ChapterId("ch2"), collapsed.Expansion.ChapterIds);
            Assert.Equal(BookViewHealth.Stale, collapsed.Health);
        }

        [Fact]
        public async Task RetryRebuildAsync_OnceTheReadRecovers_PublishesACoherentBookView()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            loader.Failure = ReadFailure();
            await RestampAsync(sut, b);

            loader.Failure = null;
            var recovered = await sut.RetryRebuildAsync();

            Assert.Equal(BookViewHealth.Coherent, recovered.Health);
            Assert.Same(sut.Snapshot, recovered);
            // The change the failed reconciliation could not show is on screen now…
            Assert.Equal(b.CharacterId("bob"), RenderedSpeakerOf(recovered, b));
            // …and the Book View is a basis for further changes again.
            Assert.IsType<BookViewMutationOutcome.Coherent>(await sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i2"), b.CharacterId("alice"))));
        }

        [Fact]
        public async Task RetryRebuildAsync_ThatFailsAgain_LeavesTheBookViewStale()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            loader.Failure = ReadFailure();
            await RestampAsync(sut, b);
            var stale = sut.Snapshot!;

            var retried = await sut.RetryRebuildAsync();

            Assert.Equal(BookViewHealth.Stale, retried.Health);
            Assert.Same(stale, retried);
        }

        /// <summary>
        /// The reader switched projects while the change was committing. The commit is real and must
        /// be reported as such, but nothing is stale: what is on screen is a coherent view of a
        /// different Book, so the outcome carries no snapshot and there is nothing to retry.
        /// </summary>
        [Fact]
        public async Task MutateAsync_WhenTheReaderMovesOffTheBookMidCommit_ReportsACommitWithNoViewOfIt()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            await SeedOtherProjectAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);

            // The switch holds the build lock across its read, so the commit below lands while this
            // projection is provably still between the two Books.
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.Held = held;
            var switching = sut.OpenAsync(_otherFolder);
            await ConvergesAsync(() => loader.Reading, "the project switch never started reading");

            var gesture = RestampAsync(sut, b);
            held.SetResult();
            await switching;

            var outcome = Assert.IsType<BookViewMutationOutcome.CommittedButStale>(await gesture);
            Assert.Null(outcome.Snapshot);
            Assert.Equal(b.CharacterId("bob"), await PersistedSpeakerOfAsync(b.ItemId("i1")));

            // Not stale — the Book View it now shows is coherent, and has nothing to recover.
            Assert.Equal(_otherFolder, sut.Snapshot!.Folder);
            Assert.Equal(BookViewHealth.Coherent, sut.Snapshot!.Health);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RetryRebuildAsync());
        }

        [Fact]
        public async Task RetryRebuildAsync_OnACoherentBookView_IsRejected()
        {
            await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);

            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RetryRebuildAsync());
        }

        /// <summary>
        /// Reopening the Book is an authoritative rebuild that also rechecks both selections, so it
        /// recovers a stale projection on the same terms as an explicit retry.
        /// </summary>
        [Fact]
        public async Task OpenAsync_OnAStaleBookView_RecoversIt()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);
            loader.Failure = ReadFailure();
            await RestampAsync(sut, b);

            loader.Failure = null;
            var reopened = await sut.OpenAsync(_folder);

            Assert.Equal(BookViewHealth.Coherent, reopened.Health);
        }

        [Fact]
        public async Task AnotherCircuitsCommit_ThatCannotBeRead_GoesStaleWithoutAnnouncingAnything()
        {
            await SeedTwoAttributableChaptersAsync();
            var loader = new SwitchableLoader(new BookProjectLoader(_reader));
            var sut = CreateSut(loader);
            var coherent = await sut.OpenAsync(_folder);

            var announced = 0;
            var updates = 0;
            sut.ExternalUpdateApplied += update => { updates++; if (update.Announce) announced++; };

            loader.Failure = ReadFailure();
            _receipts.Publish(ExternalReceipt(BookFacets.Structure));

            await ConvergesAsync(
                () => sut.Snapshot!.Health == BookViewHealth.Stale, "the failed convergence never went stale");

            // Nothing converged, so there is nothing to announce — the stale indicator is the news.
            Assert.Equal(0, updates);
            Assert.Equal(0, announced);
            Assert.Same(coherent.Branches, sut.Snapshot!.Branches);
        }

        // ── cancellation ─────────────────────────────────────────────────────

        [Fact]
        public async Task MutateAsync_CancelledBeforeCommit_ChangesNothingAndPublishesNothing()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var sut = CreateSut();
            await sut.OpenAsync(_folder);
            var published = 0;
            sut.SnapshotPublished += () => published++;

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var outcome = await sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i1"), b.CharacterId("bob")), cancelled.Token);

            var uncommitted = Assert.IsType<BookViewMutationOutcome.Uncommitted>(outcome);
            Assert.Equal(BookMutationRejection.Cancelled, uncommitted.Reason);
            Assert.NotEqual(b.CharacterId("bob"), await PersistedSpeakerOfAsync(b.ItemId("i1")));
            Assert.Equal(0, published);
            Assert.Equal(BookViewHealth.Coherent, sut.Snapshot!.Health);
        }

        /// <summary>
        /// Past the commit point the change is real, so reconciliation runs under the circuit's
        /// lifetime rather than the gesture's. Cancelling the gesture must not be able to report a
        /// committed mutation as uncommitted, nor leave the Book View showing the older Book.
        /// </summary>
        [Fact]
        public async Task MutateAsync_CancelledAfterCommit_StillReconcilesToACoherentBookView()
        {
            var b = await SeedTwoAttributableChaptersAsync();
            var (sut, loader, _) = await OpenOverABreakableLoaderAsync(b);

            // Held on the reconciliation's first read, which is the first thing past the commit.
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.Held = held;

            using var cancelled = new CancellationTokenSource();
            var gesture = sut.MutateAsync(
                new SetItemSpeakerMutation(_folder, b.ItemId("i1"), b.CharacterId("bob")), cancelled.Token);

            await ConvergesAsync(() => loader.Reading, "the commit never reached its reconciliation");
            await cancelled.CancelAsync();
            held.SetResult();

            var snapshot = Assert.IsType<BookViewMutationOutcome.Coherent>(await gesture).Snapshot;
            Assert.Equal(BookViewHealth.Coherent, snapshot.Health);
            Assert.Equal(b.CharacterId("bob"), await PersistedSpeakerOfAsync(b.ItemId("i1")));
            Assert.Equal(b.CharacterId("bob"), RenderedSpeakerOf(snapshot, b));
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


        /// <summary>
        /// A real content reader that counts what a build asked it for. It is the only way to tell a
        /// targeted refresh from a rebuild: both publish a correct snapshot, and the difference is
        /// which reads were taken to get there.
        /// </summary>
        private sealed class CountingContent(IBookContentReader inner) : IBookContentReader
        {
            public List<Guid> ChildrenOf { get; } = [];
            public List<Guid> ParagraphsRead { get; } = [];

            public Task<HierarchyChildren> GetChildrenAsync(
                ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId)
            {
                ChildrenOf.Add(parentId);
                return inner.GetChildrenAsync(folderId, parentLevel, parentId);
            }

            public Task<List<Paragraph>> GetParagraphsAsync(
                ProjectFolderId folderId, IReadOnlyCollection<Guid> paragraphIds)
            {
                ParagraphsRead.AddRange(paragraphIds);
                return inner.GetParagraphsAsync(folderId, paragraphIds);
            }

            public Task<BookOverview> GetBookOverviewAsync(ProjectFolderId f) => inner.GetBookOverviewAsync(f);
            public Task<bool> HasBookContentAsync(ProjectFolderId f) => inner.HasBookContentAsync(f);
            public Task<List<Volume>> GetVolumesAsync(ProjectFolderId f) => inner.GetVolumesAsync(f);
            public Task<List<Part>> GetPartsAsync(ProjectFolderId f, Guid v) => inner.GetPartsAsync(f, v);
            public Task<List<Chapter>> GetChaptersAsync(ProjectFolderId f, Guid p) => inner.GetChaptersAsync(f, p);
            public Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId f, Guid c) =>
                inner.GetChapterParagraphsAsync(f, c);
            public Task<int> GetTotalPartCountAsync(ProjectFolderId f) => inner.GetTotalPartCountAsync(f);
            public Task<int> GetTotalChapterCountAsync(ProjectFolderId f) => inner.GetTotalChapterCountAsync(f);
            public Task<List<(Guid ParagraphId, string Preview)>> GetOrderedParagraphsAsync(
                ProjectFolderId f, IEnumerable<Guid> ids) => inner.GetOrderedParagraphsAsync(f, ids);
            public Task<ParagraphContext?> GetParagraphContextAsync(
                ProjectFolderId f, Guid c, Guid p, int before, int after) =>
                inner.GetParagraphContextAsync(f, c, p, before, after);
            public Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
                ProjectFolderId f, Guid c, IReadOnlyList<Guid> ids, int before, int after) =>
                inner.GetParagraphBatchContextAsync(f, c, ids, before, after);
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

        public override async ValueTask DisposeAsync()
        {
            foreach (var circuit in _otherCircuits)
                await circuit.DisposeAsync();
            await _circuit.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
