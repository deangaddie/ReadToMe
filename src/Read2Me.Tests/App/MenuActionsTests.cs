using System;
using System.Threading.Tasks;
using MudBlazor;
using NSubstitute;
using Read2Me.App.Shared;
using Read2Me.App.Shared.BookMenus;
using Read2Me.Core.Models;
using Read2Me.Services;
using Xunit;
using static Read2Me.App.Shared.BookMenus.MenuActions;

namespace Read2Me.Tests.App
{
    public class MenuActionsTests
    {
        private static (MenuActions actions, IDialogService dialogs, IBookCommandHandler handler) Create()
        {
            var dialogs = Substitute.For<IDialogService>();
            var handler = Substitute.For<IBookCommandHandler>();
            return (new MenuActions(dialogs, handler), dialogs, handler);
        }

        private static IDialogReference FakeDialog(DialogResult result)
        {
            var dialogRef = Substitute.For<IDialogReference>();
            dialogRef.Result.Returns(Task.FromResult<DialogResult?>(result));
            return dialogRef;
        }

        // ---------------------------------------------------------------
        // PromptTitleAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task PromptTitleAsync_ReturnsNull_WhenCanceled()
        {
            var (actions, dialogs, _) = Create();
            var dialogRef = FakeDialog(DialogResult.Cancel());
            dialogs.ShowAsync<EditTextDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<EditTextDialog>>(), Arg.Any<DialogOptions>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.PromptTitleAsync("Title", "");

            Assert.Null(result);
        }

        [Fact]
        public async Task PromptTitleAsync_ReturnsText_WhenConfirmed()
        {
            var (actions, dialogs, _) = Create();
            var dialogRef = FakeDialog(DialogResult.Ok("My Title"));
            dialogs.ShowAsync<EditTextDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<EditTextDialog>>(), Arg.Any<DialogOptions>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.PromptTitleAsync("Title", "");

            Assert.Equal("My Title", result);
        }

        // ---------------------------------------------------------------
        // ConfirmDeleteAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ConfirmDeleteAsync_ReturnsFalse_WhenCanceled()
        {
            var (actions, dialogs, _) = Create();
            var dialogRef = FakeDialog(DialogResult.Cancel());
            dialogs.ShowAsync<ConfirmDeleteDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<ConfirmDeleteDialog>>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.ConfirmDeleteAsync("Chapter", "Ch 1", hasChildren: true);

            Assert.False(result);
        }

        [Fact]
        public async Task ConfirmDeleteAsync_ReturnsTrue_WhenConfirmed()
        {
            var (actions, dialogs, _) = Create();
            var dialogRef = FakeDialog(DialogResult.Ok(true));
            dialogs.ShowAsync<ConfirmDeleteDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<ConfirmDeleteDialog>>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.ConfirmDeleteAsync("Chapter", "Ch 1", hasChildren: true);

            Assert.True(result);
        }

        // ---------------------------------------------------------------
        // ExecuteAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task ExecuteAsync_DelegatesToHandler_WithExactCommand()
        {
            var (actions, _, handler) = Create();
            var folderId = new ProjectFolderId("test-book");
            var command = new DeleteChapterCommand(folderId, Guid.NewGuid());
            handler.ExecuteAsync(command).Returns(Task.FromResult<Guid?>(null));

            await actions.ExecuteAsync(command);

            await handler.Received(1).ExecuteAsync(command);
        }

        // ---------------------------------------------------------------
        // BuildMerge
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(NodeKind.Volume,        MergeDirection.Previous)]
        [InlineData(NodeKind.Volume,        MergeDirection.Next)]
        [InlineData(NodeKind.Part,          MergeDirection.Previous)]
        [InlineData(NodeKind.Chapter,       MergeDirection.Next)]
        [InlineData(NodeKind.Paragraph,     MergeDirection.Previous)]
        [InlineData(NodeKind.ParagraphItem, MergeDirection.Next)]
        public void BuildMerge_ReturnsCorrectCommandType(NodeKind kind, MergeDirection dir)
        {
            var folderId = new ProjectFolderId("f");
            var id = Guid.NewGuid();

            var cmd = BuildMerge(folderId, kind, id, dir);

            Assert.Equal(folderId, cmd.FolderId);
            switch (kind)
            {
                case NodeKind.Volume:        Assert.IsType<MergeVolumeCommand>(cmd);        Assert.Equal(id, ((MergeVolumeCommand)cmd).VolumeId);               break;
                case NodeKind.Part:          Assert.IsType<MergePartCommand>(cmd);          Assert.Equal(id, ((MergePartCommand)cmd).PartId);                   break;
                case NodeKind.Chapter:       Assert.IsType<MergeChapterCommand>(cmd);       Assert.Equal(id, ((MergeChapterCommand)cmd).ChapterId);             break;
                case NodeKind.Paragraph:     Assert.IsType<MergeParagraphCommand>(cmd);     Assert.Equal(id, ((MergeParagraphCommand)cmd).ParagraphId);         break;
                case NodeKind.ParagraphItem: Assert.IsType<MergeParagraphItemCommand>(cmd); Assert.Equal(id, ((MergeParagraphItemCommand)cmd).ItemId);          break;
            }
        }

        // ---------------------------------------------------------------
        // BookNodeMenuSpecs split levels
        // ---------------------------------------------------------------

        [Fact]
        public void ForPart_Split_HasVolumeLevel()
        {
            var spec = BookNodeMenuSpecs.ForPart(new ProjectFolderId("f"), new Read2Me.Data.Entities.Part { Id = Guid.NewGuid() });
            Assert.Equal(Read2Me.App.State.BookHierarchyPresenter.SplitLevel.Volume, spec.Splits[0].Level);
        }

        [Fact]
        public void ForChapter_Split_HasPartLevel()
        {
            var spec = BookNodeMenuSpecs.ForChapter(new ProjectFolderId("f"), new Read2Me.Data.Entities.Chapter { Id = Guid.NewGuid() });
            Assert.Equal(Read2Me.App.State.BookHierarchyPresenter.SplitLevel.Part, spec.Splits[0].Level);
        }

        [Fact]
        public void ForParagraph_Split_HasChapterLevel()
        {
            var spec = BookNodeMenuSpecs.ForParagraph(new ProjectFolderId("f"), new Read2Me.Data.Entities.Paragraph { Id = Guid.NewGuid() });
            Assert.Equal(Read2Me.App.State.BookHierarchyPresenter.SplitLevel.Chapter, spec.Splits[0].Level);
        }

        // ---------------------------------------------------------------
        // BuildDelete
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(NodeKind.Volume)]
        [InlineData(NodeKind.Part)]
        [InlineData(NodeKind.Chapter)]
        [InlineData(NodeKind.Paragraph)]
        [InlineData(NodeKind.ParagraphItem)]
        public void BuildDelete_ReturnsCorrectCommandType(NodeKind kind)
        {
            var folderId = new ProjectFolderId("f");
            var id = Guid.NewGuid();

            var cmd = BuildDelete(folderId, kind, id);

            Assert.Equal(folderId, cmd.FolderId);
            switch (kind)
            {
                case NodeKind.Volume:        Assert.IsType<DeleteVolumeCommand>(cmd);        Assert.Equal(id, ((DeleteVolumeCommand)cmd).VolumeId);               break;
                case NodeKind.Part:          Assert.IsType<DeletePartCommand>(cmd);          Assert.Equal(id, ((DeletePartCommand)cmd).PartId);                   break;
                case NodeKind.Chapter:       Assert.IsType<DeleteChapterCommand>(cmd);       Assert.Equal(id, ((DeleteChapterCommand)cmd).ChapterId);             break;
                case NodeKind.Paragraph:     Assert.IsType<DeleteParagraphCommand>(cmd);     Assert.Equal(id, ((DeleteParagraphCommand)cmd).ParagraphId);         break;
                case NodeKind.ParagraphItem: Assert.IsType<DeleteParagraphItemCommand>(cmd); Assert.Equal(id, ((DeleteParagraphItemCommand)cmd).ItemId);          break;
            }
        }
    }
}
