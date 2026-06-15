using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MudBlazor;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.UseCases;
using Xunit;

namespace Read2Me.Tests.State
{
    // Fake BookUseCases: controllable import results without real dependencies.
    internal class FakeBookUseCases : BookUseCases
    {
        private Result _result = Result.Ok();

        public FakeBookUseCases() : base(null!, null!, null!) { }

        public void SetResult(Result r) => _result = r;

        public override Task<Result> ImportAsync(string folderName, bool reread = false, CancellationToken ct = default)
            => Task.FromResult(_result);

        public override Task<Result> ImportManuallyAsync(string folderName, ManualReadOptions options, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    public class BookHierarchyPresenterTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private record Context(
            BookHierarchyPresenter Presenter,
            IProjectReader Reader,
            IBookCommandHandler CommandHandler,
            FakeBookUseCases BookUseCases,
            BookTreeState TreeState);

        private static Context Create()
        {
            var reader = Substitute.For<IProjectReader>();
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var bookUseCases = new FakeBookUseCases();
            var dialogService = Substitute.For<IDialogService>();

            // Default reader returns
            reader.GetProjectAsync(Folder).Returns((Project?)null);
            reader.HasBookContentAsync(Folder).Returns(false);
            reader.GetVolumesAsync(Folder).Returns(new List<Volume>());
            reader.GetCharactersAsync(Folder).Returns(new List<Character>());
            reader.GetTotalPartCountAsync(Folder).Returns(0);
            reader.GetTotalChapterCountAsync(Folder).Returns(0);

            var hierarchyLoader = new BookHierarchyLoader(reader);
            var treeState = new BookTreeState(hierarchyLoader);
            var presenter = new BookHierarchyPresenter(reader, commandHandler, bookUseCases, treeState, dialogService);
            return new Context(presenter, reader, commandHandler, bookUseCases, treeState);
        }

        // ---------------------------------------------------------------
        // LoadAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadAsync_NoContent_HasContentFalse_VolumesEmpty()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            Assert.False(ctx.Presenter.HasContent);
            Assert.Empty(ctx.Presenter.Volumes);
        }

        [Fact]
        public async Task LoadAsync_WithContent_HasContentTrue_LoadsVolumes()
        {
            var ctx = Create();
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol1", Order = "a" };
            ctx.Reader.HasBookContentAsync(Folder).Returns(true);
            ctx.Reader.GetVolumesAsync(Folder).Returns(new List<Volume> { vol });
            ctx.Reader.GetCharactersAsync(Folder).Returns(new List<Character>());
            ctx.Reader.GetTotalPartCountAsync(Folder).Returns(1);
            ctx.Reader.GetTotalChapterCountAsync(Folder).Returns(1);
            ctx.Reader.GetPartsAsync(Folder, vol.Id).Returns(new List<Part>());

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(ctx.Presenter.HasContent);
            Assert.Single(ctx.Presenter.Volumes);
        }

        [Fact]
        public async Task LoadAsync_IsLoading_FalseAfterComplete()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);
            Assert.False(ctx.Presenter.IsLoading);
        }

        // ---------------------------------------------------------------
        // ReadBookAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ReadBookAsync_Success_ErrorIsNull()
        {
            var ctx = Create();
            ctx.BookUseCases.SetResult(Result.Ok());

            await ctx.Presenter.ReadBookAsync(Folder);

            Assert.Null(ctx.Presenter.Error);
        }

        [Fact]
        public async Task ReadBookAsync_Failure_SetsError()
        {
            var ctx = Create();
            ctx.BookUseCases.SetResult(Result.Fail("Import failed"));

            await ctx.Presenter.ReadBookAsync(Folder);

            Assert.Equal("Import failed", ctx.Presenter.Error);
        }

        [Fact]
        public async Task ReadBookAsync_IsBusy_FalseAfterComplete()
        {
            var ctx = Create();
            ctx.BookUseCases.SetResult(Result.Ok());

            await ctx.Presenter.ReadBookAsync(Folder);

            Assert.False(ctx.Presenter.IsBusy);
        }

        // ---------------------------------------------------------------
        // ConfirmReread / RequestConfirmReread / CancelConfirmReread
        // ---------------------------------------------------------------

        [Fact]
        public void RequestConfirmReread_SetsConfirmRereadTrue()
        {
            var ctx = Create();
            ctx.Presenter.RequestConfirmReread();
            Assert.True(ctx.Presenter.ConfirmReread);
        }

        [Fact]
        public void CancelConfirmReread_SetsConfirmRereadFalse()
        {
            var ctx = Create();
            ctx.Presenter.RequestConfirmReread();
            ctx.Presenter.CancelConfirmReread();
            Assert.False(ctx.Presenter.ConfirmReread);
        }

        // ---------------------------------------------------------------
        // SetItemCharacterAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetItemCharacterAsync_UnknownCharacterId_SetsCharacterNull()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                CharacterId = Guid.NewGuid(),
                Character = new Character { Id = Guid.NewGuid(), Name = "Bob" },
                Order = "a"
            };

            await ctx.Presenter.SetItemCharacterAsync(Folder, item, null);
            await ctx.CommandHandler.Received(1).ExecuteAsync(Arg.Any<SetItemCharacterCommand>());

            Assert.Null(item.Character);
            Assert.Null(item.CharacterId);
        }

        [Fact]
        public async Task SetItemCharacterAsync_KnownCharacterId_SetsCharacter()
        {
            var ctx = Create();
            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "Alice" };

            ctx.Reader.HasBookContentAsync(Folder).Returns(true);
            ctx.Reader.GetVolumesAsync(Folder).Returns(new List<Volume>());
            ctx.Reader.GetCharactersAsync(Folder).Returns(new List<Character> { character });
            ctx.Reader.GetTotalPartCountAsync(Folder).Returns(0);
            ctx.Reader.GetTotalChapterCountAsync(Folder).Returns(0);
            await ctx.Presenter.LoadAsync(Folder);

            var item = new ParagraphItem { Id = Guid.NewGuid(), Order = "a" };
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, charId);

            Assert.Equal(charId, item.CharacterId);
            Assert.Equal("Alice", item.Character?.Name);
        }

        // ---------------------------------------------------------------
        // SplitAndReloadAsync — new panel expansion
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitAndReload_SourceExpanded_ExpandsNewParent()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var sourcePartId = Guid.NewGuid();
            var newPartId = Guid.NewGuid();
            ctx.Presenter.Tree.ExpandedPartIds.Add(sourcePartId);
            ctx.CommandHandler.ExecuteAsync(Arg.Any<SplitAtChapterCommand>()).Returns(newPartId);

            await ctx.Presenter.SplitAndReloadAsync(
                Folder,
                new SplitAtChapterCommand(Folder, Guid.NewGuid(), null),
                BookHierarchyPresenter.SplitLevel.Part,
                sourcePartId);

            Assert.Contains(sourcePartId, ctx.Presenter.Tree.ExpandedPartIds);
            Assert.Contains(newPartId, ctx.Presenter.Tree.ExpandedPartIds);
        }

        [Fact]
        public async Task SplitAndReload_SourceCollapsed_DoesNotExpandNewParent()
        {
            var ctx = Create();
            await ctx.Presenter.LoadAsync(Folder);

            var sourcePartId = Guid.NewGuid();
            var newPartId = Guid.NewGuid();
            // sourcePartId NOT in ExpandedPartIds
            ctx.CommandHandler.ExecuteAsync(Arg.Any<SplitAtChapterCommand>()).Returns(newPartId);

            await ctx.Presenter.SplitAndReloadAsync(
                Folder,
                new SplitAtChapterCommand(Folder, Guid.NewGuid(), null),
                BookHierarchyPresenter.SplitLevel.Part,
                sourcePartId);

            Assert.DoesNotContain(newPartId, ctx.Presenter.Tree.ExpandedPartIds);
        }

        [Fact]
        public async Task SetItemCharacterAsync_StaleCharacterList_RefreshesAndSetsCharacter()
        {
            var ctx = Create();
            // Load with empty character list
            await ctx.Presenter.LoadAsync(Folder);
            Assert.Empty(ctx.Presenter.Characters);

            var charId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "NewChar" };

            // Reader returns the character on refresh
            ctx.Reader.GetCharactersAsync(Folder).Returns(new List<Character> { character });

            var item = new ParagraphItem { Id = Guid.NewGuid(), Order = "a" };
            await ctx.Presenter.SetItemCharacterAsync(Folder, item, charId);

            Assert.Equal(charId, item.CharacterId);
            Assert.Equal("NewChar", item.Character?.Name);
            await ctx.Reader.Received().GetCharactersAsync(Folder);
        }

        // ---------------------------------------------------------------
        // StateChanged event
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadAsync_RaisesStateChanged()
        {
            var ctx = Create();
            bool raised = false;
            ctx.Presenter.StateChanged += () => raised = true;

            await ctx.Presenter.LoadAsync(Folder);

            Assert.True(raised);
        }
    }
}
