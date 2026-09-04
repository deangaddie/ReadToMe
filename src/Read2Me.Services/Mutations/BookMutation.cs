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
// one family — the destructive half (delete, merge, clear) migrates separately.

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
