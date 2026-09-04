using MudBlazor;
using NSubstitute;
using Read2Me.App.Shared;
using Read2Me.App.Shared.BookMenus;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.App;

/// <summary>
/// The producer's insert gesture on the ParagraphItem menu: which anchors offer it, and what the
/// entries build. The dialog itself is <see cref="EditTextDialog"/>, whose Save button is already
/// disabled while the field is whitespace-only; the whitespace tests here cover the second guard —
/// even a confirm carrying blank text builds no mutation.
/// </summary>
public class BookNodeMenuInsertItemTests
{
    static readonly ProjectFolderId Folder = new("test-book");

    static ParagraphItem Speech() =>
        new() { Id = Guid.NewGuid(), ItemType = ParagraphItemType.Speech, Text = "Hello there." };

    static BookNodeMenuSpec SpecFor(ParagraphItem item) =>
        BookNodeMenuSpecs.ForParagraphItem(Folder, item, null!);

    /// <summary>A MenuActions whose text prompt answers with <paramref name="answer"/>, or cancels when null.</summary>
    static (MenuActions actions, DialogParameters<EditTextDialog>?[] shown) PromptingWith(string? answer)
    {
        var dialogs = Substitute.For<IDialogService>();
        var dialogRef = Substitute.For<IDialogReference>();
        dialogRef.Result.Returns(Task.FromResult<DialogResult?>(
            answer == null ? DialogResult.Cancel() : DialogResult.Ok(answer)));

        var captured = new DialogParameters<EditTextDialog>?[1];
        dialogs.ShowAsync<EditTextDialog>(Arg.Any<string>(), Arg.Any<DialogParameters<EditTextDialog>>(), Arg.Any<DialogOptions>())
               .Returns(call =>
               {
                   captured[0] = call.Arg<DialogParameters<EditTextDialog>>();
                   return Task.FromResult(dialogRef);
               });

        return (new MenuActions(dialogs, Substitute.For<IBookCommandHandler>()), captured);
    }

    // ---------------------------------------------------------------
    // Which anchors offer the gesture
    // ---------------------------------------------------------------

    [Fact]
    public void SpeechAnchor_OffersBeforeAndAfter()
    {
        var entries = SpecFor(Speech()).InsertItems;

        Assert.Collection(entries,
            e => { Assert.Equal("Insert Item Before", e.Label); Assert.Equal(InsertPosition.Before, e.Position); },
            e => { Assert.Equal("Insert Item After", e.Label); Assert.Equal(InsertPosition.After, e.Position); });
    }

    [Theory]
    [InlineData(ParagraphItemType.Pause)]
    [InlineData(ParagraphItemType.ParagraphPause)]
    [InlineData(ParagraphItemType.ChapterPause)]
    [InlineData(ParagraphItemType.PartPause)]
    [InlineData(ParagraphItemType.VolumePause)]
    public void PauseAnchor_OffersNoInsertEntries(ParagraphItemType type)
    {
        // The pause branch of ParagraphRow renders this same spec, and a Speech item inside a
        // pause paragraph is a structure the rest of the tree assumes cannot exist.
        var pause = new ParagraphItem { Id = Guid.NewGuid(), ItemType = type };

        Assert.Empty(SpecFor(pause).InsertItems);
    }

    [Theory]
    [InlineData(ParagraphItemType.Pause)]
    [InlineData(ParagraphItemType.Speech)]
    public void PauseInsertSubmenus_AreUnchangedByThisFeature(ParagraphItemType type)
    {
        var spec = SpecFor(new ParagraphItem { Id = Guid.NewGuid(), ItemType = type });

        Assert.Equal(5, spec.InsertPausesBefore.Count);
        Assert.Equal(5, spec.InsertPausesAfter.Count);
    }

    // ---------------------------------------------------------------
    // What the entries build
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0, InsertPosition.Before)]
    [InlineData(1, InsertPosition.After)]
    public async Task Confirming_BuildsTheCommandForThatAnchorAndPosition(int index, InsertPosition expected)
    {
        var anchor = Speech();
        var (actions, _) = PromptingWith("And who might you be?");

        var mutation = await SpecFor(anchor).InsertItems[index].Build(actions);

        var insert = Assert.IsType<InsertParagraphItemMutation>(mutation);
        Assert.Equal(Folder, insert.FolderId);
        Assert.Equal(anchor.Id, insert.AnchorItemId);
        Assert.Equal(expected, insert.Position);
        Assert.Equal("And who might you be?", insert.Text);
    }

    [Fact]
    public async Task Confirming_TrimsTheText()
    {
        var (actions, _) = PromptingWith("   Padded line.\n  ");

        var mutation = await SpecFor(Speech()).InsertItems[1].Build(actions);

        Assert.Equal("Padded line.", Assert.IsType<InsertParagraphItemMutation>(mutation).Text);
    }

    [Fact]
    public async Task Cancelling_BuildsNothing()
    {
        var (actions, _) = PromptingWith(null);

        Assert.Null(await SpecFor(Speech()).InsertItems[0].Build(actions));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task WhitespaceOnlyText_BuildsNothing(string text)
    {
        var (actions, _) = PromptingWith(text);

        Assert.Null(await SpecFor(Speech()).InsertItems[1].Build(actions));
    }

    // ---------------------------------------------------------------
    // The dialog it opens
    // ---------------------------------------------------------------

    [Fact]
    public async Task OpensAnEmptyFourLineTextDialog()
    {
        var (actions, shown) = PromptingWith("Something.");

        await SpecFor(Speech()).InsertItems[0].Build(actions);

        Assert.Equal(4, shown[0]!.Get<int>(nameof(EditTextDialog.Lines)));
        Assert.Equal("", shown[0]!.Get<string>(nameof(EditTextDialog.InitialValue)));
    }
}
