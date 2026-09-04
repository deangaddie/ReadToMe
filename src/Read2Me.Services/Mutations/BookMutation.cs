using Read2Me.Core.Models;

namespace Read2Me.Services.Mutations;

/// <summary>
/// One user or domain operation against a Book, even when it changes several records.
/// A mutation is a request, not an effect: what it actually changed is reported by the
/// implementation that applies it (<see cref="BookMutationEffects"/>) and carried to readers
/// on a <see cref="BookMutationReceipt"/>.
/// <para>
/// This is the write-side sibling of <see cref="BookCommand"/>. During the migration described by
/// ADR 0007 both exist: <see cref="BookCommand"/> remains the wire and legacy-caller shape, and
/// each family that migrates gains a mutation here. The legacy façade is deleted once no caller
/// remains.
/// </para>
/// </summary>
public abstract record BookMutation(ProjectFolderId FolderId)
{
    /// <summary>The mutation's identity for receipts and logs — its type name.</summary>
    public string Name => GetType().Name;
}

/// <summary>
/// Creates one Speech ParagraphItem next to <c>AnchorItemId</c>, inside the anchor's own Paragraph.
/// The new item is born unattributed; see <see cref="InsertParagraphItemCommand"/> for why.
/// </summary>
public sealed record InsertParagraphItemMutation(
    ProjectFolderId FolderId,
    Guid AnchorItemId,
    InsertPosition Position,
    string Text) : BookMutation(FolderId);

// ── additive structural mutations ────────────────────────────────────────────
// Every one of these creates nodes: a split makes a new parent beside the source, and the title
// and pause additions make new Paragraphs. None of them deletes anything, which is what makes them
// one family — the destructive half is below.

/// <summary>Splits the Volume holding <c>PartId</c> so that Part and every later sibling start a new one.</summary>
public sealed record SplitAtPartMutation(
    ProjectFolderId FolderId, Guid PartId, string? NewVolumeTitle) : BookMutation(FolderId);

/// <summary>Splits the Part holding <c>ChapterId</c> so that Chapter and every later sibling start a new one.</summary>
public sealed record SplitAtChapterMutation(
    ProjectFolderId FolderId, Guid ChapterId, string? NewPartTitle) : BookMutation(FolderId);

/// <summary>Splits the Chapter holding <c>ParagraphId</c> so that Paragraph and every later sibling start a new one.</summary>
public sealed record SplitAtParagraphMutation(
    ProjectFolderId FolderId, Guid ParagraphId, string? NewChapterTitle) : BookMutation(FolderId);

/// <summary>Splits the Paragraph holding <c>ItemId</c> so that item and every later sibling start a new one.</summary>
public sealed record SplitAtItemMutation(ProjectFolderId FolderId, Guid ItemId) : BookMutation(FolderId);

/// <summary>Puts the project's book title and author at the very front of the Book, as narration.</summary>
public sealed record AddBookTitleMutation(ProjectFolderId FolderId) : BookMutation(FolderId);

/// <summary>Gives every titled Volume a leading Chapter that speaks its title.</summary>
public sealed record AddVolumeTitlesMutation(ProjectFolderId FolderId) : BookMutation(FolderId);

/// <summary>Gives every titled Part a leading Chapter that speaks its title.</summary>
public sealed record AddPartTitlesMutation(ProjectFolderId FolderId) : BookMutation(FolderId);

/// <summary>Puts a spoken title Paragraph at the front of every titled Chapter.</summary>
public sealed record AddChapterTitlesMutation(ProjectFolderId FolderId) : BookMutation(FolderId);

/// <summary>Inserts every pause the Book's structure implies and does not already have.</summary>
public sealed record AddPausesMutation(ProjectFolderId FolderId) : BookMutation(FolderId);

/// <summary>Creates one pause Paragraph next to the Paragraph holding <c>AnchorItemId</c>.</summary>
public sealed record InsertPauseParagraphMutation(
    ProjectFolderId FolderId,
    Guid AnchorItemId,
    InsertPosition Position,
    PauseKind PauseKind) : BookMutation(FolderId);

// ── destructive structural mutations ─────────────────────────────────────────
// The other half of the structural family: every one of these removes nodes. A merge folds a node
// into a sibling and deletes it, a delete removes a subtree, and clearing removes the Book's whole
// content. What makes them one family is that a reader can be looking at something that is about to
// stop existing — so reconciliation has to drop selections and expansion, not just recount.

/// <summary>Folds a Volume into the sibling in <c>Direction</c>, which keeps the Volume's Parts.</summary>
public sealed record MergeVolumeMutation(
    ProjectFolderId FolderId, Guid VolumeId, MergeDirection Direction) : BookMutation(FolderId);

/// <summary>Folds a Part into the sibling in <c>Direction</c>, which keeps the Part's Chapters.</summary>
public sealed record MergePartMutation(
    ProjectFolderId FolderId, Guid PartId, MergeDirection Direction) : BookMutation(FolderId);

/// <summary>Folds a Chapter into the sibling in <c>Direction</c>, which keeps the Chapter's Paragraphs.</summary>
public sealed record MergeChapterMutation(
    ProjectFolderId FolderId, Guid ChapterId, MergeDirection Direction) : BookMutation(FolderId);

/// <summary>Folds a Paragraph into the sibling in <c>Direction</c>, which keeps the Paragraph's items.</summary>
public sealed record MergeParagraphMutation(
    ProjectFolderId FolderId, Guid ParagraphId, MergeDirection Direction) : BookMutation(FolderId);

/// <summary>Joins a ParagraphItem's text onto the sibling in <c>Direction</c> and deletes it.</summary>
public sealed record MergeParagraphItemMutation(
    ProjectFolderId FolderId, Guid ItemId, MergeDirection Direction) : BookMutation(FolderId);

/// <summary>Deletes a Volume and everything under it.</summary>
public sealed record DeleteVolumeMutation(ProjectFolderId FolderId, Guid VolumeId) : BookMutation(FolderId);

/// <summary>Deletes a Part and everything under it.</summary>
public sealed record DeletePartMutation(ProjectFolderId FolderId, Guid PartId) : BookMutation(FolderId);

/// <summary>Deletes a Chapter and everything under it.</summary>
public sealed record DeleteChapterMutation(ProjectFolderId FolderId, Guid ChapterId) : BookMutation(FolderId);

/// <summary>Deletes a Paragraph and its items.</summary>
public sealed record DeleteParagraphMutation(ProjectFolderId FolderId, Guid ParagraphId) : BookMutation(FolderId);

/// <summary>Deletes one ParagraphItem.</summary>
public sealed record DeleteParagraphItemMutation(ProjectFolderId FolderId, Guid ItemId) : BookMutation(FolderId);

/// <summary>
/// Removes the Book's entire content — every Volume, Part, Chapter, Paragraph and item — leaving
/// the project, its Characters and its Voices. The reread and manual-reread imports clear before
/// they rebuild.
/// </summary>
public sealed record ClearBookContentMutation(ProjectFolderId FolderId) : BookMutation(FolderId);
