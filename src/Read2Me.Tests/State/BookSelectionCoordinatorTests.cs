using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.State
{
    public class BookSelectionCoordinatorTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private record Context(
            BookSelectionCoordinator Coordinator,
            IProjectReader Reader,
            CharacterQueueService CharacterQueue,
            AudioQueueService AudioQueue,
            ISnackbar Snackbar,
            BookSelectionState SelectionState,
            AudioItemSelectionState AudioSelectionState)
        {
            public FolderSelection Selection => SelectionState.For(Folder);
            public AudioItemSelection AudioSelection => AudioSelectionState.For(Folder);
        }

        private static Context Create(bool hasTtsConfig = false)
        {
            var reader = Substitute.For<IProjectReader>();
            var characterQueue = new CharacterQueueService();
            var audioQueue = new AudioQueueService();
            var paragraphTtsSettings = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
            var snackbar = Substitute.For<ISnackbar>();
            var selectionState = new BookSelectionState();
            var audioSelectionState = new AudioItemSelectionState();

            reader.GetCharacterParagraphsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>());

            reader.GetAudioItemRefsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<BookNodeLevel>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new List<AudioItemRef>());

            reader.GetOrderedParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<IEnumerable<Guid>>())
                .Returns(new List<(Guid ParagraphId, string Preview)>());

            reader.GetOrderedAudioItemRefsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<IEnumerable<Guid>>())
                .Returns(new List<AudioItemRef>());

            Read2Me.AppData.Entities.ParagraphTtsServiceConfig? config =
                hasTtsConfig ? new Read2Me.AppData.Entities.ParagraphTtsServiceConfig() : null;
            paragraphTtsSettings.GetActiveConfigAsync().Returns(config);

            var coordinator = new BookSelectionCoordinator(
                reader, characterQueue, audioQueue, paragraphTtsSettings, snackbar,
                selectionState, audioSelectionState);

            return new Context(coordinator, reader, characterQueue, audioQueue, snackbar, selectionState, audioSelectionState);
        }

        // Prime _lastFolder without adding to selection
        private static async Task SetFolder(BookSelectionCoordinator coordinator)
        {
            var pId = Guid.NewGuid();
            await coordinator.ToggleParagraphAsync(Folder, pId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), on: true);
            await coordinator.ToggleParagraphAsync(Folder, pId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), on: false);
        }

        // ---------------------------------------------------------------
        // ToggleParagraphAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ToggleParagraphAsync_On_SelectsParagraph()
        {
            var ctx = Create();
            var pId = Guid.NewGuid();

            await ctx.Coordinator.ToggleParagraphAsync(Folder, pId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), on: true);

            Assert.True(ctx.Selection.IsParagraphSelected(pId));
        }

        [Fact]
        public async Task ToggleParagraphAsync_Off_DeselectedParagraph()
        {
            var ctx = Create();
            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();

            await ctx.Coordinator.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);
            await ctx.Coordinator.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: false);

            Assert.False(ctx.Selection.IsParagraphSelected(pId));
        }

        // ---------------------------------------------------------------
        // SetNodeAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetNodeAsync_On_CallsReaderAndAddsRefs()
        {
            var ctx = Create();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var pId1 = Guid.NewGuid(); var pId2 = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef>
                {
                    new CharacterParagraphRef(pId1, chId, ptId, volId),
                    new CharacterParagraphRef(pId2, chId, ptId, volId),
                });

            await ctx.Coordinator.SetNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);

            Assert.True(ctx.Selection.IsParagraphSelected(pId1));
            Assert.True(ctx.Selection.IsParagraphSelected(pId2));
            await ctx.Reader.Received(1).GetCharacterParagraphsAsync(Folder, BookNodeLevel.Chapter, chId, false);
        }

        [Fact]
        public async Task SetNodeAsync_Off_RemovesRefs()
        {
            var ctx = Create();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var pId = Guid.NewGuid();

            ctx.Reader.GetCharacterParagraphsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>())
                .Returns(new List<CharacterParagraphRef> { new CharacterParagraphRef(pId, chId, ptId, volId) });

            await ctx.Coordinator.SetNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);
            await ctx.Coordinator.SetNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: false);

            Assert.False(ctx.Selection.IsParagraphSelected(pId));
        }

        // ---------------------------------------------------------------
        // AddSelectionToCharacterQueueAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task AddSelectionToCharacterQueueAsync_EmptySelection_NoOp()
        {
            var ctx = Create();
            await SetFolder(ctx.Coordinator);

            await ctx.Coordinator.AddSelectionToCharacterQueueAsync();

            await ctx.Reader.DidNotReceive().GetOrderedParagraphsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<IEnumerable<Guid>>());
        }

        [Fact]
        public async Task AddSelectionToCharacterQueueAsync_WithSelection_EnqueuesItems()
        {
            var ctx = Create();
            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();

            await ctx.Coordinator.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);

            ctx.Reader.GetOrderedParagraphsAsync(Folder, Arg.Any<IEnumerable<Guid>>())
                .Returns(new List<(Guid, string)> { (pId, "preview line") });

            await ctx.Coordinator.AddSelectionToCharacterQueueAsync();

            Assert.Equal(ParagraphQueueStatus.Queued, ctx.CharacterQueue.StatusOf(Folder, pId));
        }

        [Fact]
        public async Task AddSelectionToCharacterQueueAsync_WithSelection_ClearsSelectionAfterDrain()
        {
            var ctx = Create();
            var pId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();

            await ctx.Coordinator.ToggleParagraphAsync(Folder, pId, chId, ptId, volId, on: true);
            Assert.Equal(1, ctx.Coordinator.SelectedParagraphCount);

            ctx.Reader.GetOrderedParagraphsAsync(Folder, Arg.Any<IEnumerable<Guid>>())
                .Returns(new List<(Guid, string)> { (pId, "preview") });

            await ctx.Coordinator.AddSelectionToCharacterQueueAsync();

            Assert.Equal(0, ctx.Coordinator.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // ToggleAudioItemAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ToggleAudioItemAsync_On_SelectsItem()
        {
            var ctx = Create();
            var itemId = Guid.NewGuid(); var paraId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var item = new AudioItemRef(itemId, paraId, chId, ptId, volId);

            await SetFolder(ctx.Coordinator);
            await ctx.Coordinator.ToggleAudioItemAsync(item, on: true);

            Assert.True(ctx.AudioSelection.IsItemSelected(itemId));
        }

        [Fact]
        public async Task ToggleAudioItemAsync_Off_DeselectedItem()
        {
            var ctx = Create();
            var itemId = Guid.NewGuid(); var paraId = Guid.NewGuid();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var item = new AudioItemRef(itemId, paraId, chId, ptId, volId);

            await SetFolder(ctx.Coordinator);
            await ctx.Coordinator.ToggleAudioItemAsync(item, on: true);
            await ctx.Coordinator.ToggleAudioItemAsync(item, on: false);

            Assert.False(ctx.AudioSelection.IsItemSelected(itemId));
        }

        // ---------------------------------------------------------------
        // SetAudioNodeAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetAudioNodeAsync_On_CallsReaderWithNarratorMode_AndAddsItems()
        {
            var ctx = Create();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var itemId = Guid.NewGuid(); var paraId = Guid.NewGuid();

            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>(), narratorOnlyMode: true)
                .Returns(new List<AudioItemRef> { new AudioItemRef(itemId, paraId, chId, ptId, volId) });

            await ctx.Coordinator.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true, narratorOnlyMode: true);

            Assert.True(ctx.AudioSelection.IsItemSelected(itemId));
            await ctx.Reader.Received(1).GetAudioItemRefsAsync(
                Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>(), narratorOnlyMode: true);
        }

        [Fact]
        public async Task SetAudioNodeAsync_Off_RemovesItems()
        {
            var ctx = Create();
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var itemId = Guid.NewGuid(); var paraId = Guid.NewGuid();

            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new List<AudioItemRef> { new AudioItemRef(itemId, paraId, chId, ptId, volId) });

            await ctx.Coordinator.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);
            await ctx.Coordinator.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: false);

            Assert.False(ctx.AudioSelection.IsItemSelected(itemId));
        }

        // ---------------------------------------------------------------
        // AddSelectionToAudioQueueAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task AddSelectionToAudioQueueAsync_NoTtsConfig_ShowsSnackbar_NoEnqueue()
        {
            var ctx = Create(hasTtsConfig: false);
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var itemId = Guid.NewGuid(); var paraId = Guid.NewGuid();

            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new List<AudioItemRef> { new AudioItemRef(itemId, paraId, chId, ptId, volId) });
            await ctx.Coordinator.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);

            await ctx.Coordinator.AddSelectionToAudioQueueAsync();

            ctx.Snackbar.Received(1).Add(
                Arg.Any<string>(), Severity.Warning,
                Arg.Any<Action<SnackbarOptions>?>(), Arg.Any<string?>());
            await ctx.Reader.DidNotReceive().GetOrderedAudioItemRefsAsync(
                Arg.Any<ProjectFolderId>(), Arg.Any<IEnumerable<Guid>>());
        }

        [Fact]
        public async Task AddSelectionToAudioQueueAsync_WithTtsConfig_DrainsThenClears()
        {
            var ctx = Create(hasTtsConfig: true);
            var chId = Guid.NewGuid(); var ptId = Guid.NewGuid(); var volId = Guid.NewGuid();
            var itemId = Guid.NewGuid(); var paraId = Guid.NewGuid();
            var item = new AudioItemRef(itemId, paraId, chId, ptId, volId);

            ctx.Reader.GetAudioItemRefsAsync(Folder, BookNodeLevel.Chapter, chId, Arg.Any<bool>(), Arg.Any<bool>())
                .Returns(new List<AudioItemRef> { item });
            ctx.Reader.GetOrderedAudioItemRefsAsync(Folder, Arg.Any<IEnumerable<Guid>>())
                .Returns(new List<AudioItemRef> { item });

            await ctx.Coordinator.SetAudioNodeAsync(Folder, BookNodeLevel.Chapter, chId, on: true);
            Assert.Equal(1, ctx.Coordinator.SelectedAudioItemCount);

            await ctx.Coordinator.AddSelectionToAudioQueueAsync();

            Assert.Equal(0, ctx.Coordinator.SelectedAudioItemCount);
            Assert.Equal(AudioItemQueueStatus.Queued, ctx.AudioQueue.StatusOf(Folder, itemId));
        }
    }
}
