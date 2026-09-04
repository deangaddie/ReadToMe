using System;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Shared.BookMenus;

public sealed class MenuActions(IDialogService dialogs, IBookCommandHandler handler)
{
    static readonly DialogOptions EditOpts = new() { MaxWidth = MaxWidth.Medium, FullWidth = true };

    public async Task<string?> PromptTitleAsync(string heading, string initial)
    {
        var dialog = await dialogs.ShowAsync<EditTextDialog>(heading,
            new DialogParameters<EditTextDialog>
            {
                { d => d.Label, "Title" },
                { d => d.InitialValue, initial },
            }, EditOpts);
        var result = await dialog.Result;
        if (result?.Canceled != false) return null;
        return result.Data as string;
    }

    public async Task<string?> PromptTextAsync(string heading, string initial, int lines = 1)
    {
        var dialog = await dialogs.ShowAsync<EditTextDialog>(heading,
            new DialogParameters<EditTextDialog>
            {
                { d => d.Label, "Text" },
                { d => d.InitialValue, initial },
                { d => d.Lines, lines },
            }, EditOpts);
        var result = await dialog.Result;
        if (result?.Canceled != false) return null;
        return result.Data as string;
    }

    public async Task<bool> ConfirmDeleteAsync(string itemType, string itemName, bool hasChildren)
    {
        var dialog = await dialogs.ShowAsync<ConfirmDeleteDialog>($"Delete {itemType}",
            new DialogParameters<ConfirmDeleteDialog>
            {
                { d => d.ItemType, itemType },
                { d => d.ItemName, itemName },
                { d => d.HasChildren, hasChildren },
            });
        var result = await dialog.Result;
        return result?.Canceled == false;
    }

    /// <summary>The legacy path, for the menu gestures whose family has not migrated yet.</summary>
    public Task<Guid?> ExecuteAsync(BookCommand command) => handler.ExecuteAsync(command);

    // ── Mutation factories ───────────────────────────────────────────────────
    // Both gestures are the same entry at every level of the tree, so the node's kind picks the
    // mutation here rather than five near-identical menus doing it themselves.

    public static BookMutation BuildMerge(ProjectFolderId folderId, NodeKind kind, Guid id, MergeDirection dir) => kind switch
    {
        NodeKind.Volume        => new MergeVolumeMutation(folderId, id, dir),
        NodeKind.Part          => new MergePartMutation(folderId, id, dir),
        NodeKind.Chapter       => new MergeChapterMutation(folderId, id, dir),
        NodeKind.Paragraph     => new MergeParagraphMutation(folderId, id, dir),
        NodeKind.ParagraphItem => new MergeParagraphItemMutation(folderId, id, dir),
        _                      => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static BookMutation BuildDelete(ProjectFolderId folderId, NodeKind kind, Guid id) => kind switch
    {
        NodeKind.Volume        => new DeleteVolumeMutation(folderId, id),
        NodeKind.Part          => new DeletePartMutation(folderId, id),
        NodeKind.Chapter       => new DeleteChapterMutation(folderId, id),
        NodeKind.Paragraph     => new DeleteParagraphMutation(folderId, id),
        NodeKind.ParagraphItem => new DeleteParagraphItemMutation(folderId, id),
        _                      => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public enum NodeKind { Volume, Part, Chapter, Paragraph, ParagraphItem }
