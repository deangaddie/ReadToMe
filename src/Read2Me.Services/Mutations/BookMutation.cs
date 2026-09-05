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

// ── audio assignment and reviews ─────────────────────────────────────────────
// The Audio Queue's half of the high-frequency traffic, and the same shape as speaker attribution:
// one known item, one known Paragraph, no node created or moved. A queue run writes one of these
// per item, so a Book View that rebuilt for each would spend the run rereading a Book that gained
// one WAV.
//
// Recording is one mutation rather than two because the audio and the verdict on it are one fact.
// Committing them separately is how a Book View comes to show a played row whose review chip still
// describes the previous take.

/// <summary>
/// What the pipeline concluded about a take: whether normalisation and verification passed, and the
/// evidence a person needs to judge it themselves. Both stages passing is the absence of a review,
/// not a review that says "fine" — row presence <em>is</em> the needs-review signal.
/// </summary>
public sealed record AudioReviewVerdict(
    bool NormalizeOk,
    string? NormalizeReason,
    bool VerifyOk,
    double? Wer,
    string? VerifyReason,
    string? Transcript,
    string? OriginalTextSnapshot);

/// <summary>
/// Records one generated take: the item's audio reference and the verdict on it, together.
/// <para>
/// The WAV is staged on disk before this commits and moved into place after it, so the persisted
/// Book never names an artifact that is not finished (ADR 0007).
/// </para>
/// </summary>
public sealed record RecordParagraphItemAudioMutation(
    ProjectFolderId FolderId,
    Guid ItemId,
    string RelativePath,
    AudioReviewVerdict Verdict) : BookMutation(FolderId);

/// <summary>Points one item at an audio file, with no verdict — the generic command endpoint's half.</summary>
public sealed record SetParagraphItemAudioMutation(
    ProjectFolderId FolderId, Guid ItemId, string RelativePath) : BookMutation(FolderId);

/// <summary>Records a verdict on an item's existing audio without touching the audio itself.</summary>
public sealed record SetAudioReviewMutation(
    ProjectFolderId FolderId, Guid ItemId, AudioReviewVerdict Verdict) : BookMutation(FolderId);

/// <summary>
/// The reader's "I have listened to it and it is fine": the review stops asking without the take
/// being regenerated. A fresh failure resets it to needs-review.
/// </summary>
public sealed record DismissAudioReviewMutation(ProjectFolderId FolderId, Guid ItemId) : BookMutation(FolderId);

// ── character, narrator and policy lifecycles ────────────────────────────────
// The roster and who narrates. Nothing here names a Paragraph, and almost everything here changes
// what a Paragraph *means*: a merge moves every line the merged character spoke, a delete hands its
// lines back to the queue, a narrator link changes whose voice the narration is read in, and
// NarratorOnlyMode changes which items are audio-eligible at all. So this family is reconciled by
// rebuilding rather than by rereading named rows — the effect is Book-wide even when the write is
// one row.
//
// The seed Narrator row is protected throughout. It is not a character someone invented, it is the
// unlinked state of narration (ADR-0004), so renaming, deleting or merging it is a refusal rather
// than a no-op.

/// <summary>
/// Creates a Character, unless one already answers to <c>CharacterName</c> by canonical name or alias — the
/// roster is keyed by what a speaker is called, so creating a name that already resolves changes
/// nothing.
/// </summary>
public sealed record CreateCharacterMutation(ProjectFolderId FolderId, string CharacterName) : BookMutation(FolderId);

/// <summary>Renames a Character. Its aliases, Voices and lines are untouched.</summary>
public sealed record RenameCharacterMutation(
    ProjectFolderId FolderId, Guid CharacterId, string CharacterName) : BookMutation(FolderId);

/// <summary>Gives a Character another name it answers to, if it does not answer to it already.</summary>
public sealed record AddCharacterAliasMutation(
    ProjectFolderId FolderId, Guid CharacterId, string AliasName) : BookMutation(FolderId);

/// <summary>Takes one alias away from whichever Character owns it.</summary>
public sealed record RemoveCharacterAliasMutation(ProjectFolderId FolderId, Guid AliasId) : BookMutation(FolderId);

/// <summary>
/// Declares two Characters to be one person: every line, alias and — when asked — the merged name
/// itself moves to the survivor, the merged Character's Voices and Voice Rules die with it, and a
/// narrator link on the merged side follows the survivor.
/// </summary>
public sealed record MergeCharactersMutation(
    ProjectFolderId FolderId,
    Guid SurvivorId,
    Guid MergedId,
    bool AddNameAsAlias) : BookMutation(FolderId);

/// <summary>
/// Removes a Character. Its lines survive as unattributed dialog for the queue to answer again; its
/// aliases, Voices and Voice Rules do not, and a narrator link to it is cleared in the same
/// transaction so the delete and the unlink cannot half-land.
/// </summary>
public sealed record DeleteCharacterMutation(ProjectFolderId FolderId, Guid CharacterId) : BookMutation(FolderId);

/// <summary>
/// Says which Character narrates this Book, or unlinks narration from the roster with null
/// (ADR-0004). The seed Narrator row cannot narrate itself — that <em>is</em> the unlinked state.
/// </summary>
public sealed record SetNarratorCharacterMutation(
    ProjectFolderId FolderId, Guid? CharacterId) : BookMutation(FolderId);

/// <summary>
/// Turns the Book-wide narrator-only policy on or off. It changes no row on any item and still
/// changes what every item is: which items the Audio Queue may speak, and therefore the audio
/// denominators and the Audio Item Selection's eligibility.
/// </summary>
public sealed record SetNarratorOnlyModeMutation(
    ProjectFolderId FolderId, bool Enabled) : BookMutation(FolderId);

// ── Voice and Voice Rule lifecycles ──────────────────────────────────────────
// The Voices a Character can be read in, and the positional rules that pick between them. Nothing
// here names a Paragraph and nothing here writes one: a Voice row and a Voice Rule row are the only
// things these touch. What makes the family matter to a Book View is indirect — every item that
// resolves through a changed Voice or rule now previews a different name — so the effects report
// Voices and VoiceRules and let the reader reread the previews it holds.
//
// The default Voice Rule is the invariant this family protects. A Character with Voices has exactly
// one, it sorts below every positional rule, and it is created, repointed and removed by the Voice
// mutations rather than by anyone editing rules directly.

/// <summary>
/// Adds a Voice to a Character, and — when it is the Character's first — the default Voice Rule that
/// makes it the one every position falls back to.
/// <para>
/// <c>Description</c> and <c>DesignPrompt</c> are here so the voice-plan batch can land a planned
/// Voice in one commit instead of three. The generic command endpoint leaves both null and sets them
/// afterwards, because <see cref="CreateVoiceCommand"/>'s shape is fixed.
/// </para>
/// </summary>
public sealed record CreateVoiceMutation(
    ProjectFolderId FolderId,
    Guid CharacterId,
    string VoiceName,
    bool IsGenerated = false,
    string? Description = null,
    string? DesignPrompt = null) : BookMutation(FolderId);

/// <summary>Points the Character's default Voice Rule at this Voice.</summary>
public sealed record SetVoiceDefaultMutation(ProjectFolderId FolderId, Guid VoiceId) : BookMutation(FolderId);

/// <summary>Renames a Voice and rewrites its description. The audio it names is untouched.</summary>
public sealed record UpdateVoiceMutation(
    ProjectFolderId FolderId, Guid VoiceId, string VoiceName, string? Description) : BookMutation(FolderId);

/// <summary>Stores the description a generated Voice is synthesised from.</summary>
public sealed record SetVoiceDesignPromptMutation(
    ProjectFolderId FolderId, Guid VoiceId, string Prompt) : BookMutation(FolderId);

/// <summary>Overrides the voice-design server's settings for this Voice, or clears the override with null.</summary>
public sealed record SetVoiceDesignSettingsOverrideMutation(
    ProjectFolderId FolderId, Guid VoiceId, string? Json) : BookMutation(FolderId);

/// <summary>Overrides the TTS server's settings for this Voice, or clears the override with null.</summary>
public sealed record SetVoiceTtsSettingsOverrideMutation(
    ProjectFolderId FolderId, Guid VoiceId, string? Json) : BookMutation(FolderId);

/// <summary>Stores what the Voice's reference audio actually says — what a cloning TTS is given with it.</summary>
public sealed record SetVoiceTranscriptMutation(
    ProjectFolderId FolderId, Guid VoiceId, string Transcript) : BookMutation(FolderId);

/// <summary>
/// Points a Voice at reference audio.
/// <para>
/// Like <see cref="RecordParagraphItemAudioMutation"/> this never reports no-change: an upload lands
/// at a path derived from the Voice's id and name, so re-uploading writes the same string over
/// different audio. The path is a name, not the artifact.
/// </para>
/// </summary>
public sealed record SetVoiceAudioMutation(
    ProjectFolderId FolderId, Guid VoiceId, string AudioFileName) : BookMutation(FolderId);

/// <summary>
/// Records a synthesised take of a designed Voice: the audio, the sample text it speaks, and the
/// prompt it was designed from, in one commit. Never reports no-change, for the same reason
/// <see cref="SetVoiceAudioMutation"/> does not.
/// </summary>
public sealed record SetVoiceGeneratedMutation(
    ProjectFolderId FolderId,
    Guid VoiceId,
    string AudioFileName,
    string Transcript,
    string DesignPrompt) : BookMutation(FolderId);

/// <summary>
/// Switches a Voice between cloned-from-a-recording and designed-from-a-description. Turning a
/// Voice generated discards the recording it was cloned from — there is nothing left to clone — and
/// with it the stored original that would otherwise claim an edit on audio that no longer exists.
/// </summary>
public sealed record SetVoiceSourceMutation(
    ProjectFolderId FolderId, Guid VoiceId, bool IsGenerated) : BookMutation(FolderId);

/// <summary>
/// Removes a Voice, its audio, its stored original and every rule that named it. The Character's
/// default Voice Rule follows to its oldest remaining Voice, or goes too when that was the last one.
/// </summary>
public sealed record DeleteVoiceMutation(ProjectFolderId FolderId, Guid VoiceId) : BookMutation(FolderId);

/// <summary>
/// Adds a positional Voice Rule below every rule the Character already has: from a node onward, up
/// to a node, or one node exactly. The Voice must be one of that Character's own.
/// </summary>
public sealed record CreateVoiceRuleMutation(
    ProjectFolderId FolderId,
    Guid CharacterId,
    Guid VoiceId,
    VoiceAnchorLevel? FromLevel,
    Guid? FromNodeId,
    VoiceAnchorLevel? ToLevel,
    Guid? ToNodeId) : BookMutation(FolderId);

/// <summary>Removes one positional Voice Rule. The default rule is not one of these.</summary>
public sealed record DeleteVoiceRuleMutation(ProjectFolderId FolderId, Guid RuleId) : BookMutation(FolderId);

/// <summary>
/// Moves one positional Voice Rule past its neighbour. Rules are evaluated in order, so this is how
/// a reader decides which of two overlapping rules wins.
/// </summary>
public sealed record MoveVoiceRuleMutation(
    ProjectFolderId FolderId, Guid RuleId, RuleMoveDirection Direction) : BookMutation(FolderId);
