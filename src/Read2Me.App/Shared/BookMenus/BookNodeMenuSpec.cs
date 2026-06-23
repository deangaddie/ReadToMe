using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.App.State;

namespace Read2Me.App.Shared.BookMenus;

public readonly record struct BookSplit(
    BookCommand Command,
    BookHierarchyPresenter.SplitLevel Level,
    Guid SourceParentId);

public sealed record BookNodeMenuSpec(
    ProjectFolderId FolderId,
    NodeKind Kind,
    Guid EntityId,
    string? EditLabel,
    Func<MenuActions, Task<(BookCommand? command, Action? updateLocal)>>? EditAction,
    IReadOnlyList<SplitSpec> Splits,
    string DeleteLabel,
    bool DeleteCallsChanged,
    bool MergeResetsTree,
    Func<(string itemType, string itemName, bool hasChildren)> GetDeleteConfirmArgs
)
{
    public IReadOnlyList<InsertPauseSpec> InsertPausesBefore { get; init; } = [];
    public IReadOnlyList<InsertPauseSpec> InsertPausesAfter { get; init; } = [];
}

/// <summary>
/// Describes a split action for a node menu item.
/// <para>
/// When <see cref="BuildHierarchySplit"/> is non-null the split creates a new parent node;
/// the component fires <c>OnSplit</c> with a fully-formed <see cref="BookSplit"/>.
/// When <see cref="BuildDirectCommand"/> is non-null the command is executed directly and
/// <c>OnReset</c> is fired instead (used for ParagraphItem splits).
/// </para>
/// </summary>
public sealed record SplitSpec(
    string Label,
    BookHierarchyPresenter.SplitLevel Level,
    Func<MenuActions, Task<(BookCommand? command, Guid parentId)>>? BuildHierarchySplit,
    Func<MenuActions, Task<BookCommand?>>? BuildDirectCommand
)
{
    public static SplitSpec Hierarchy(
        string label,
        BookHierarchyPresenter.SplitLevel level,
        Func<MenuActions, Task<(BookCommand?, Guid)>> build) =>
        new(label, level, build, null);

    public static SplitSpec Direct(string label, Func<MenuActions, Task<BookCommand?>> build) =>
        new(label, default, null, build);
}

public sealed record InsertPauseSpec(string Label, PauseKind PauseKind);

public static class BookNodeMenuSpecs
{
    public static BookNodeMenuSpec ForVolume(ProjectFolderId folderId, Volume volume) =>
        new(
            FolderId: folderId,
            Kind: NodeKind.Volume,
            EntityId: volume.Id,
            EditLabel: "Edit Title",
            EditAction: async menu =>
            {
                var text = await menu.PromptTitleAsync("Edit Volume Title", volume.Title ?? "");
                if (string.IsNullOrWhiteSpace(text)) return (null, null);
                return (new UpdateVolumeTitleCommand(folderId, volume.Id, text), () => { volume.Title = text; });
            },
            Splits: [],
            DeleteLabel: "Delete Volume",
            DeleteCallsChanged: false,
            MergeResetsTree: false,
            GetDeleteConfirmArgs: () => ("Volume", volume.Title ?? "Volume", true)
        );

    public static BookNodeMenuSpec ForPart(ProjectFolderId folderId, Part part) =>
        new(
            FolderId: folderId,
            Kind: NodeKind.Part,
            EntityId: part.Id,
            EditLabel: "Edit Title",
            EditAction: async menu =>
            {
                var text = await menu.PromptTitleAsync("Edit Part Title", part.Title ?? "");
                if (string.IsNullOrWhiteSpace(text)) return (null, null);
                return (new UpdatePartTitleCommand(folderId, part.Id, text), () => { part.Title = text; });
            },
            Splits:
            [
                SplitSpec.Hierarchy("Split Volume", BookHierarchyPresenter.SplitLevel.Volume, async menu =>
                {
                    var title = await menu.PromptTitleAsync("New Volume Title", "");
                    if (title == null) return (null, default);
                    return (new SplitAtPartCommand(folderId, part.Id, string.IsNullOrWhiteSpace(title) ? null : title), part.VolumeId);
                })
            ],
            DeleteLabel: "Delete Part",
            DeleteCallsChanged: false,
            MergeResetsTree: false,
            GetDeleteConfirmArgs: () => ("Part", part.Title ?? "Part", true)
        );

    public static BookNodeMenuSpec ForChapter(ProjectFolderId folderId, Chapter chapter) =>
        new(
            FolderId: folderId,
            Kind: NodeKind.Chapter,
            EntityId: chapter.Id,
            EditLabel: "Edit Title",
            EditAction: async menu =>
            {
                var text = await menu.PromptTitleAsync("Edit Chapter Title", chapter.Title ?? "");
                if (string.IsNullOrWhiteSpace(text)) return (null, null);
                return (new UpdateChapterTitleCommand(folderId, chapter.Id, text), () => { chapter.Title = text; });
            },
            Splits:
            [
                SplitSpec.Hierarchy("Split Part", BookHierarchyPresenter.SplitLevel.Part, async menu =>
                {
                    var title = await menu.PromptTitleAsync("New Part Title", "");
                    if (title == null) return (null, default);
                    return (new SplitAtChapterCommand(folderId, chapter.Id, string.IsNullOrWhiteSpace(title) ? null : title), chapter.PartId);
                })
            ],
            DeleteLabel: "Delete Chapter",
            DeleteCallsChanged: false,
            MergeResetsTree: false,
            GetDeleteConfirmArgs: () => ("Chapter", chapter.Title ?? "Chapter", true)
        );

    public static BookNodeMenuSpec ForParagraph(ProjectFolderId folderId, Paragraph paragraph) =>
        new(
            FolderId: folderId,
            Kind: NodeKind.Paragraph,
            EntityId: paragraph.Id,
            EditLabel: null,
            EditAction: null,
            Splits:
            [
                SplitSpec.Hierarchy("Split Chapter", BookHierarchyPresenter.SplitLevel.Chapter, async menu =>
                {
                    var title = await menu.PromptTitleAsync("New Chapter Title", "");
                    if (title == null) return (null, default);
                    return (new SplitAtParagraphCommand(folderId, paragraph.Id, string.IsNullOrWhiteSpace(title) ? null : title), paragraph.ChapterId);
                })
            ],
            DeleteLabel: "Delete Paragraph",
            DeleteCallsChanged: true,
            MergeResetsTree: true,
            GetDeleteConfirmArgs: () =>
            {
                var t = string.Join(" ", System.Linq.Enumerable.Select(paragraph.Items, i => i.Text));
                var label = t.Length > 60 ? t[..60] + "…" : t;
                if (string.IsNullOrWhiteSpace(label)) label = "this paragraph";
                return ("Paragraph", label, false);
            }
        );

    public static BookNodeMenuSpec ForPauseParagraph(ProjectFolderId folderId, Paragraph paragraph) =>
        new(
            FolderId: folderId,
            Kind: NodeKind.Paragraph,
            EntityId: paragraph.Id,
            EditLabel: null,
            EditAction: null,
            Splits: [],
            DeleteLabel: "Delete Pause",
            DeleteCallsChanged: true,
            MergeResetsTree: false,
            GetDeleteConfirmArgs: () =>
            {
                var label = ParagraphItemDisplay.GetPauseLabel(paragraph.Items.FirstOrDefault()?.ItemType);
                return ("Pause", label, false);
            }
        );

    static readonly IReadOnlyList<InsertPauseSpec> PauseSpecs =
    [
        new("Pause",          PauseKind.Pause),
        new("Paragraph Pause", PauseKind.ParagraphPause),
        new("Chapter Pause",  PauseKind.ChapterPause),
        new("Part Pause",     PauseKind.PartPause),
        new("Volume Pause",   PauseKind.VolumePause),
    ];

    public static BookNodeMenuSpec ForParagraphItem(ProjectFolderId folderId, ParagraphItem item) =>
        new(
            FolderId: folderId,
            Kind: NodeKind.ParagraphItem,
            EntityId: item.Id,
            EditLabel: "Edit Text",
            EditAction: async menu =>
            {
                var text = await menu.PromptTextAsync("Edit Item Text", item.Text ?? "", lines: 4);
                if (string.IsNullOrWhiteSpace(text)) return (null, null);
                return (new UpdateParagraphItemTextCommand(folderId, item.Id, text), () => { item.Text = text; });
            },
            Splits:
            [
                SplitSpec.Direct("Split Paragraph", _ =>
                    Task.FromResult<BookCommand?>(new SplitAtItemCommand(folderId, item.Id)))
            ],
            DeleteLabel: "Delete Item",
            DeleteCallsChanged: false,
            MergeResetsTree: true,
            GetDeleteConfirmArgs: () =>
            {
                var label = item.Text?.Length > 60 ? item.Text[..60] + "…" : item.Text ?? "this item";
                return ("Item", label, false);
            }
        ) { InsertPausesBefore = PauseSpecs, InsertPausesAfter = PauseSpecs };
}
