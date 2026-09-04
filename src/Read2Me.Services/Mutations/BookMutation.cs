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

    /// <summary>
    /// Who asked for this mutation, when the producer needs to recognise the resulting receipt as
    /// its own. A Book View projection stamps its own id here, because the receipt reaches every
    /// open projection and the initiating one has already reconciled: it must not announce a change
    /// back to the person who just made it (ADR 0007).
    /// <para>
    /// <see cref="Guid.Empty"/> means unattributed, which is what every producer that does not
    /// observe receipts leaves it as. It is an origin marker and nothing more — the write side
    /// neither validates it nor behaves differently for any value.
    /// </para>
    /// </summary>
    public Guid OriginId { get; init; }
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

// ── speaker attribution ──────────────────────────────────────────────────────
// The high-frequency family: nothing here creates, deletes, reorders or retypes an item — item
// boundaries are frozen (ADR 0005) — so every one of these can name exactly which Paragraphs and
// ParagraphItems it restamped. That exactness is the point: a queue attributing a chapter must be
// able to refresh the rows it touched without every open Book View rereading its expanded branches.

/// <summary>
/// Stamps one item's speaker by hand — any speaker on any speech item (ADR-0006). A hand-flip is an
/// explicit "this is the wrong voice", so it also discards the item's generated audio.
/// </summary>
public sealed record SetItemSpeakerMutation(
    ProjectFolderId FolderId, Guid ItemId, Guid? CharacterId) : BookMutation(FolderId);

/// <summary>
/// Stamps a speaker across one Paragraph's dialog, leaving its narration alone — unless there is no
/// dialog left, which is what makes assigning a whole Paragraph to the narrator reversible.
/// </summary>
public sealed record SetParagraphSpeakerMutation(
    ProjectFolderId FolderId,
    Guid ParagraphId,
    Guid? CharacterId,
    string? VoiceInstructions = null) : BookMutation(FolderId);

/// <summary>
/// The bulk sibling: one speaker across the dialog of every listed Paragraph. Narration is never
/// swept — a blind fan-out across a selection must not turn a chapter's narration into dialog.
/// </summary>
public sealed record SetParagraphsSpeakerMutation(
    ProjectFolderId FolderId, IReadOnlyList<Guid> ParagraphIds, Guid? CharacterId) : BookMutation(FolderId);

/// <summary>
/// The Character Queue's answer applied to one Paragraph's existing items. Unlike a hand-flip this
/// keeps generated audio: see ADR-0006 for why that asymmetry stands.
/// </summary>
public sealed record AttributeParagraphItemsMutation(
    ProjectFolderId FolderId,
    Guid ParagraphId,
    IReadOnlyList<ItemAttribution> Items) : BookMutation(FolderId);
