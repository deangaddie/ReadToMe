using MudBlazor;
using NSubstitute;
using Read2Me.App.Shared;
using Read2Me.App.Shared.BookMenus;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;
using Xunit;
using static Read2Me.App.Shared.BookMenus.MenuActions;

namespace Read2Me.Tests.App
{
    public class MenuActionsTests
    {
        private static (MenuActions actions, IDialogService dialogs) Create()
        {
            var dialogs = Substitute.For<IDialogService>();
            return (new MenuActions(dialogs), dialogs);
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
            var (actions, dialogs) = Create();
            var dialogRef = FakeDialog(DialogResult.Cancel());
            dialogs.ShowAsync<EditTextDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<EditTextDialog>>(), Arg.Any<DialogOptions>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.PromptTitleAsync("Title", "");

            Assert.Null(result);
        }

        [Fact]
        public async Task PromptTitleAsync_ReturnsText_WhenConfirmed()
        {
            var (actions, dialogs) = Create();
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
            var (actions, dialogs) = Create();
            var dialogRef = FakeDialog(DialogResult.Cancel());
            dialogs.ShowAsync<ConfirmDeleteDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<ConfirmDeleteDialog>>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.ConfirmDeleteAsync("Chapter", "Ch 1", hasChildren: true);

            Assert.False(result);
        }

        [Fact]
        public async Task ConfirmDeleteAsync_ReturnsTrue_WhenConfirmed()
        {
            var (actions, dialogs) = Create();
            var dialogRef = FakeDialog(DialogResult.Ok(true));
            dialogs.ShowAsync<ConfirmDeleteDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<ConfirmDeleteDialog>>())
                   .Returns(Task.FromResult(dialogRef));

            var result = await actions.ConfirmDeleteAsync("Chapter", "Ch 1", hasChildren: true);

            Assert.True(result);
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
        public void BuildMerge_ReturnsCorrectMutationType(NodeKind kind, MergeDirection dir)
        {
            var folderId = new ProjectFolderId("f");
            var id = Guid.NewGuid();

            var mutation = BuildMerge(folderId, kind, id, dir);

            Assert.Equal(folderId, mutation.FolderId);
            switch (kind)
            {
                case NodeKind.Volume:        Assert.Equal(id, Assert.IsType<MergeVolumeMutation>(mutation).VolumeId);              break;
                case NodeKind.Part:          Assert.Equal(id, Assert.IsType<MergePartMutation>(mutation).PartId);                  break;
                case NodeKind.Chapter:       Assert.Equal(id, Assert.IsType<MergeChapterMutation>(mutation).ChapterId);            break;
                case NodeKind.Paragraph:     Assert.Equal(id, Assert.IsType<MergeParagraphMutation>(mutation).ParagraphId);        break;
                case NodeKind.ParagraphItem: Assert.Equal(id, Assert.IsType<MergeParagraphItemMutation>(mutation).ItemId);         break;
            }
            Assert.Equal(dir, mutation switch
            {
                MergeVolumeMutation m => m.Direction,
                MergePartMutation m => m.Direction,
                MergeChapterMutation m => m.Direction,
                MergeParagraphMutation m => m.Direction,
                MergeParagraphItemMutation m => m.Direction,
                _ => throw new InvalidOperationException($"Not a merge: {mutation.Name}"),
            });
        }

        // ---------------------------------------------------------------
        // BookNodeMenuSpecs splits — which mutation each node's entry builds
        // ---------------------------------------------------------------

        /// <summary>A MenuActions whose text prompt always answers <paramref name="answer"/>.</summary>
        private static MenuActions PromptingWith(string answer)
        {
            var (actions, dialogs) = Create();
            var dialogRef = FakeDialog(DialogResult.Ok(answer));
            dialogs.ShowAsync<EditTextDialog>(
                       Arg.Any<string>(), Arg.Any<DialogParameters<EditTextDialog>>(), Arg.Any<DialogOptions>())
                   .Returns(Task.FromResult(dialogRef));
            return actions;
        }

        private static MenuActions PromptingWithATitle() => PromptingWith("New Title");

        [Fact]
        public async Task ForPart_Split_BuildsAVolumeSplitAtThatPart()
        {
            var partId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForPart(new ProjectFolderId("f"), new Read2Me.Data.Entities.Part { Id = partId });

            var mutation = await spec.Splits[0].Build(PromptingWithATitle());

            var split = Assert.IsType<SplitAtPartMutation>(mutation);
            Assert.Equal(partId, split.PartId);
            Assert.Equal("New Title", split.NewVolumeTitle);
        }

        [Fact]
        public async Task ForChapter_Split_BuildsAPartSplitAtThatChapter()
        {
            var chapterId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForChapter(new ProjectFolderId("f"), new Read2Me.Data.Entities.Chapter { Id = chapterId });

            var mutation = await spec.Splits[0].Build(PromptingWithATitle());

            Assert.Equal(chapterId, Assert.IsType<SplitAtChapterMutation>(mutation).ChapterId);
        }

        [Fact]
        public async Task ForParagraph_Split_BuildsAChapterSplitAtThatParagraph()
        {
            var paragraphId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForParagraph(
                new ProjectFolderId("f"), new Read2Me.Data.Entities.Paragraph { Id = paragraphId });

            var mutation = await spec.Splits[0].Build(PromptingWithATitle());

            Assert.Equal(paragraphId, Assert.IsType<SplitAtParagraphMutation>(mutation).ParagraphId);
        }

        // ---------------------------------------------------------------
        // BookNodeMenuSpecs edits — which mutation each node's Edit entry builds
        //
        // Nothing here patches the entity it was built from: the edit crosses BookMutations and the
        // Book View reads the new wording back from the persisted Book (ADR 0007), so what the spec
        // must get right is naming the node and carrying the text the producer typed.
        // ---------------------------------------------------------------

        [Fact]
        public async Task ForVolume_Edit_BuildsAVolumeTitleUpdate()
        {
            var volumeId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForVolume(
                new ProjectFolderId("f"), new Read2Me.Data.Entities.Volume { Id = volumeId, Title = "Old" });

            var mutation = await spec.EditAction!(PromptingWithATitle());

            var update = Assert.IsType<UpdateVolumeTitleMutation>(mutation);
            Assert.Equal(volumeId, update.VolumeId);
            Assert.Equal("New Title", update.Title);
        }

        [Fact]
        public async Task ForPart_Edit_BuildsAPartTitleUpdate()
        {
            var partId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForPart(
                new ProjectFolderId("f"), new Read2Me.Data.Entities.Part { Id = partId, Title = "Old" });

            var mutation = await spec.EditAction!(PromptingWithATitle());

            Assert.Equal(partId, Assert.IsType<UpdatePartTitleMutation>(mutation).PartId);
        }

        [Fact]
        public async Task ForChapter_Edit_BuildsAChapterTitleUpdate()
        {
            var chapterId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForChapter(
                new ProjectFolderId("f"), new Read2Me.Data.Entities.Chapter { Id = chapterId, Title = "Old" });

            var mutation = await spec.EditAction!(PromptingWithATitle());

            Assert.Equal(chapterId, Assert.IsType<UpdateChapterTitleMutation>(mutation).ChapterId);
        }

        [Fact]
        public async Task ForParagraphItem_Edit_BuildsAnItemTextUpdate()
        {
            var itemId = Guid.NewGuid();
            var spec = BookNodeMenuSpecs.ForParagraphItem(
                new ProjectFolderId("f"), new Read2Me.Data.Entities.ParagraphItem { Id = itemId, Text = "Old" });

            var mutation = await spec.EditAction!(PromptingWith("Rewritten"));

            var update = Assert.IsType<UpdateParagraphItemTextMutation>(mutation);
            Assert.Equal(itemId, update.ItemId);
            Assert.Equal("Rewritten", update.Text);
        }

        /// <summary>
        /// A cancelled or blank prompt sends nothing. Text that came back unchanged is not filtered
        /// here any more — the mutation answers NoChange, which costs no revision and no
        /// reconciliation, and is the one answer that holds however many circuits are open.
        /// </summary>
        [Fact]
        public async Task ForParagraphItem_Edit_SendsNothing_WhenTheProducerCancelled()
        {
            var (actions, dialogs) = Create();
            var dialogRef = FakeDialog(DialogResult.Cancel());
            dialogs.ShowAsync<EditTextDialog>(
                       Arg.Any<string>(), Arg.Any<DialogParameters<EditTextDialog>>(), Arg.Any<DialogOptions>())
                   .Returns(Task.FromResult(dialogRef));
            var spec = BookNodeMenuSpecs.ForParagraphItem(
                new ProjectFolderId("f"), new Read2Me.Data.Entities.ParagraphItem { Id = Guid.NewGuid(), Text = "Old" });

            Assert.Null(await spec.EditAction!(actions));
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
        public void BuildDelete_ReturnsCorrectMutationType(NodeKind kind)
        {
            var folderId = new ProjectFolderId("f");
            var id = Guid.NewGuid();

            var mutation = BuildDelete(folderId, kind, id);

            Assert.Equal(folderId, mutation.FolderId);
            switch (kind)
            {
                case NodeKind.Volume:        Assert.Equal(id, Assert.IsType<DeleteVolumeMutation>(mutation).VolumeId);             break;
                case NodeKind.Part:          Assert.Equal(id, Assert.IsType<DeletePartMutation>(mutation).PartId);                 break;
                case NodeKind.Chapter:       Assert.Equal(id, Assert.IsType<DeleteChapterMutation>(mutation).ChapterId);           break;
                case NodeKind.Paragraph:     Assert.Equal(id, Assert.IsType<DeleteParagraphMutation>(mutation).ParagraphId);       break;
                case NodeKind.ParagraphItem: Assert.Equal(id, Assert.IsType<DeleteParagraphItemMutation>(mutation).ItemId);        break;
            }
        }
    }
}
