using Read2Me.Core.Models;
using Read2Me.Data.Enums;

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

    /// <summary>
    /// Whether this mutation changes <b>Project state</b> — the user's choices a History step fixes
    /// — rather than only results and progress (context/history.md). Declared by the family a
    /// mutation derives from, <see cref="ProjectStateMutation"/> or <see cref="ResultsOnlyMutation"/>,
    /// so a new mutation cannot compile without choosing.
    /// </summary>
    public abstract bool ChangesProjectState { get; }
}

/// <summary>
/// A mutation that changes Project state: book content, the roster, Voices and their reference
/// audio, Voice Rules, narrator and project settings, cover and metadata. History captures it.
/// </summary>
public abstract record ProjectStateMutation(ProjectFolderId FolderId) : BookMutation(FolderId)
{
    public sealed override bool ChangesProjectState => true;

    /// <summary>
    /// The state files this mutation's producer wrote over in place before it commits — a Voice's
    /// reference audio or stored original, the cover — which History hashes into its blobs with
    /// the commit (ADR 0016, "Files"). A file the Book names for the first time, or stops naming,
    /// is seen from the rows and needs no declaration; one rewritten under a name it already had is
    /// seen only through this, so a file staged for another commit is never pinned on this one.
    /// </summary>
    public virtual StateFileOwners WritesFiles => StateFileOwners.None;

    /// <summary>The History step a <b>Restore</b> brings back, which its step records; null for any other mutation.</summary>
    internal virtual Guid? RestoresStepId => null;

    /// <summary>
    /// The step's "what changed" when the mutation says it better than its receipt's ids can — read
    /// after the mutation applied, so it can count what it did. Null describes the step from the receipt.
    /// </summary>
    internal virtual string? StepSummary => null;
}

/// <summary>
/// Whose state files a mutation writes over: some Voices' audio and originals, and the cover — or
/// every state file, the source book file included, for a Restore.
/// </summary>
public sealed record StateFileOwners(IReadOnlyCollection<Guid> Voices, bool Cover = false, bool Every = false)
{
    public static readonly StateFileOwners None = new([]);
    public static readonly StateFileOwners TheCover = new([], Cover: true);
    public static readonly StateFileOwners All = new([], Cover: true, Every: true);

    public static StateFileOwners Voice(Guid voiceId) => new([voiceId]);
}

/// <summary>
/// A mutation that writes only results — a generated take, the review of it, its transcription
/// score — or progress, a Pipeline checkpoint. History never captures one: a Restore keeps the live
/// results instead of rewinding them.
/// </summary>
public abstract record ResultsOnlyMutation(ProjectFolderId FolderId) : BookMutation(FolderId)
{
    public sealed override bool ChangesProjectState => false;
}

/// <summary>
/// Creates one Speech ParagraphItem next to <c>AnchorItemId</c>, inside the anchor's own Paragraph.
/// The new item is born unattributed; see <see cref="InsertParagraphItemCommand"/> for why.
/// </summary>
public sealed record InsertParagraphItemMutation(
    ProjectFolderId FolderId,
    Guid AnchorItemId,
    InsertPosition Position,
    string Text) : ProjectStateMutation(FolderId);

/// <summary>
/// Creates one node beside or inside the anchor, its kind following from the anchor and the
/// placement; see <see cref="InsertNodeCommand"/>.
/// </summary>
public sealed record InsertNodeMutation(
    ProjectFolderId FolderId,
    NodeKind AnchorKind,
    Guid AnchorId,
    Placement Placement,
    string? Title,
    string? Text) : ProjectStateMutation(FolderId);

// ── additive structural mutations ────────────────────────────────────────────
// Every one of these creates nodes: a split makes a new parent beside the source, and the title
// and pause additions make new Paragraphs. None of them deletes anything, which is what makes them
// one family — the destructive half is below.

/// <summary>Splits the Volume holding <c>PartId</c> so that Part and every later sibling start a new one.</summary>
public sealed record SplitAtPartMutation(
    ProjectFolderId FolderId, Guid PartId, string? NewVolumeTitle) : ProjectStateMutation(FolderId);

/// <summary>Splits the Part holding <c>ChapterId</c> so that Chapter and every later sibling start a new one.</summary>
public sealed record SplitAtChapterMutation(
    ProjectFolderId FolderId, Guid ChapterId, string? NewPartTitle) : ProjectStateMutation(FolderId);

/// <summary>Splits the Chapter holding <c>ParagraphId</c> so that Paragraph and every later sibling start a new one.</summary>
public sealed record SplitAtParagraphMutation(
    ProjectFolderId FolderId, Guid ParagraphId, string? NewChapterTitle) : ProjectStateMutation(FolderId);

/// <summary>Splits the Paragraph holding <c>ItemId</c> so that item and every later sibling start a new one.</summary>
public sealed record SplitAtItemMutation(ProjectFolderId FolderId, Guid ItemId) : ProjectStateMutation(FolderId);

/// <summary>Puts the project's book title and author at the very front of the Book, as narration.</summary>
public sealed record AddBookTitleMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

/// <summary>Gives every titled Volume a leading Chapter that speaks its title.</summary>
public sealed record AddVolumeTitlesMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

/// <summary>Gives every titled Part a leading Chapter that speaks its title.</summary>
public sealed record AddPartTitlesMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

/// <summary>Puts a spoken title Paragraph at the front of every titled Chapter.</summary>
public sealed record AddChapterTitlesMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

/// <summary>Inserts every pause the Book's structure implies and does not already have.</summary>
public sealed record AddPausesMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

/// <summary>Creates one pause Paragraph next to the Paragraph holding <c>AnchorItemId</c>.</summary>
public sealed record InsertPauseParagraphMutation(
    ProjectFolderId FolderId,
    Guid AnchorItemId,
    InsertPosition Position,
    PauseKind PauseKind) : ProjectStateMutation(FolderId);

// ── destructive structural mutations ─────────────────────────────────────────
// The other half of the structural family: every one of these removes nodes. A merge folds a node
// into a sibling and deletes it, a delete removes a subtree, and clearing removes the Book's whole
// content. What makes them one family is that a reader can be looking at something that is about to
// stop existing — so reconciliation has to drop selections and expansion, not just recount.

/// <summary>Folds a Volume into the sibling in <c>Direction</c>, which keeps the Volume's Parts.</summary>
public sealed record MergeVolumeMutation(
    ProjectFolderId FolderId, Guid VolumeId, MergeDirection Direction) : ProjectStateMutation(FolderId);

/// <summary>Folds a Part into the sibling in <c>Direction</c>, which keeps the Part's Chapters.</summary>
public sealed record MergePartMutation(
    ProjectFolderId FolderId, Guid PartId, MergeDirection Direction) : ProjectStateMutation(FolderId);

/// <summary>Folds a Chapter into the sibling in <c>Direction</c>, which keeps the Chapter's Paragraphs.</summary>
public sealed record MergeChapterMutation(
    ProjectFolderId FolderId, Guid ChapterId, MergeDirection Direction) : ProjectStateMutation(FolderId);

/// <summary>Folds a Paragraph into the sibling in <c>Direction</c>, which keeps the Paragraph's items.</summary>
public sealed record MergeParagraphMutation(
    ProjectFolderId FolderId, Guid ParagraphId, MergeDirection Direction) : ProjectStateMutation(FolderId);

/// <summary>Joins a ParagraphItem's text onto the sibling in <c>Direction</c> and deletes it.</summary>
public sealed record MergeParagraphItemMutation(
    ProjectFolderId FolderId, Guid ItemId, MergeDirection Direction) : ProjectStateMutation(FolderId);

/// <summary>Deletes a Volume and everything under it.</summary>
public sealed record DeleteVolumeMutation(ProjectFolderId FolderId, Guid VolumeId) : ProjectStateMutation(FolderId);

/// <summary>Deletes a Part and everything under it.</summary>
public sealed record DeletePartMutation(ProjectFolderId FolderId, Guid PartId) : ProjectStateMutation(FolderId);

/// <summary>Deletes a Chapter and everything under it.</summary>
public sealed record DeleteChapterMutation(ProjectFolderId FolderId, Guid ChapterId) : ProjectStateMutation(FolderId);

/// <summary>Deletes a Paragraph and its items.</summary>
public sealed record DeleteParagraphMutation(ProjectFolderId FolderId, Guid ParagraphId) : ProjectStateMutation(FolderId);

/// <summary>Deletes one ParagraphItem.</summary>
public sealed record DeleteParagraphItemMutation(ProjectFolderId FolderId, Guid ItemId) : ProjectStateMutation(FolderId);

/// <summary>
/// Removes the Book's entire content — every Volume, Part, Chapter, Paragraph and item — leaving
/// the project, its Characters and its Voices. The reread and manual-reread imports clear before
/// they rebuild.
/// </summary>
public sealed record ClearBookContentMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

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
    ProjectFolderId FolderId, Guid ItemId, Guid? CharacterId) : ProjectStateMutation(FolderId);

/// <summary>
/// Stamps a speaker across one Paragraph's dialog, leaving its narration alone — unless there is no
/// dialog left, which is what makes assigning a whole Paragraph to the narrator reversible.
/// </summary>
public sealed record SetParagraphSpeakerMutation(
    ProjectFolderId FolderId,
    Guid ParagraphId,
    Guid? CharacterId,
    string? VoiceInstructions = null) : ProjectStateMutation(FolderId);

/// <summary>
/// The bulk sibling: one speaker across the dialog of every listed Paragraph. Narration is never
/// swept — a blind fan-out across a selection must not turn a chapter's narration into dialog.
/// </summary>
public sealed record SetParagraphsSpeakerMutation(
    ProjectFolderId FolderId, IReadOnlyList<Guid> ParagraphIds, Guid? CharacterId) : ProjectStateMutation(FolderId);

/// <summary>
/// Release narration: the narrator stamp, and the audio, come off every item of the scope's
/// narration-only paragraphs, which leaves them unattributed dialog. It is the bulk form of clearing
/// an item's speaker, so it discards audio the way a hand-flip does.
/// </summary>
public sealed record ReleaseNarrationMutation(
    ProjectFolderId FolderId, NarrationReleaseScope Scope, Guid NodeId) : ProjectStateMutation(FolderId);

/// <summary>
/// The Character Queue's answer applied to one Paragraph's existing items. Unlike a hand-flip this
/// keeps generated audio: see ADR-0006 for why that asymmetry stands.
/// </summary>
public sealed record AttributeParagraphItemsMutation(
    ProjectFolderId FolderId,
    Guid ParagraphId,
    IReadOnlyList<ItemAttribution> Items) : ProjectStateMutation(FolderId);

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
    AudioReviewVerdict Verdict) : ResultsOnlyMutation(FolderId);

/// <summary>Points one item at an audio file, with no verdict — the generic command endpoint's half.</summary>
public sealed record SetParagraphItemAudioMutation(
    ProjectFolderId FolderId, Guid ItemId, string RelativePath) : ResultsOnlyMutation(FolderId);

/// <summary>Records a verdict on an item's existing audio without touching the audio itself.</summary>
public sealed record SetAudioReviewMutation(
    ProjectFolderId FolderId, Guid ItemId, AudioReviewVerdict Verdict) : ResultsOnlyMutation(FolderId);

/// <summary>
/// The reader's "I have listened to it and it is fine": the review stops asking without the take
/// being regenerated. A fresh failure resets it to needs-review.
/// </summary>
public sealed record DismissAudioReviewMutation(ProjectFolderId FolderId, Guid ItemId) : ResultsOnlyMutation(FolderId);

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
public sealed record CreateCharacterMutation(ProjectFolderId FolderId, string CharacterName) : ProjectStateMutation(FolderId);

/// <summary>Renames a Character. Its aliases, Voices and lines are untouched.</summary>
public sealed record RenameCharacterMutation(
    ProjectFolderId FolderId, Guid CharacterId, string CharacterName) : ProjectStateMutation(FolderId);

/// <summary>Gives a Character another name it answers to, if it does not answer to it already.</summary>
public sealed record AddCharacterAliasMutation(
    ProjectFolderId FolderId, Guid CharacterId, string AliasName) : ProjectStateMutation(FolderId);

/// <summary>Takes one alias away from whichever Character owns it.</summary>
public sealed record RemoveCharacterAliasMutation(ProjectFolderId FolderId, Guid AliasId) : ProjectStateMutation(FolderId);

/// <summary>
/// Declares two Characters to be one person: every line, alias and — when asked — the merged name
/// itself moves to the survivor, the merged Character's Voices and Voice Rules die with it, and a
/// narrator link on the merged side follows the survivor.
/// </summary>
public sealed record MergeCharactersMutation(
    ProjectFolderId FolderId,
    Guid SurvivorId,
    Guid MergedId,
    bool AddNameAsAlias) : ProjectStateMutation(FolderId);

/// <summary>
/// Removes a Character. Its lines survive as unattributed dialog for the queue to answer again; its
/// aliases, Voices and Voice Rules do not, and a narrator link to it is cleared in the same
/// transaction so the delete and the unlink cannot half-land.
/// </summary>
public sealed record DeleteCharacterMutation(ProjectFolderId FolderId, Guid CharacterId) : ProjectStateMutation(FolderId);

/// <summary>
/// Says which Character narrates this Book, or unlinks narration from the roster with null
/// (ADR-0004). The seed Narrator row cannot narrate itself — that <em>is</em> the unlinked state.
/// </summary>
public sealed record SetNarratorCharacterMutation(
    ProjectFolderId FolderId, Guid? CharacterId) : ProjectStateMutation(FolderId);

/// <summary>
/// Turns the Book-wide narrator-only policy on or off. It changes no row on any item and still
/// changes what every item is: which items the Audio Queue may speak, and therefore the audio
/// denominators and the Audio Item Selection's eligibility.
/// </summary>
public sealed record SetNarratorOnlyModeMutation(
    ProjectFolderId FolderId, bool Enabled) : ProjectStateMutation(FolderId);

/// <summary>
/// Confirms or skips a Pipeline step, or reopens it with a null state. Not Book content, but it
/// commits like any mutation so the write lock serializes it and a receipt carries it to every
/// open Overview.
/// </summary>
public sealed record SetCheckpointMutation(
    ProjectFolderId FolderId, PipelineStep Step, CheckpointState? State) : ResultsOnlyMutation(FolderId);

// ── project details ──────────────────────────────────────────────────────────
// The project row's own fields: what the project and its book are called, who wrote it, and the
// cover. None of it is on a Paragraph, and the cover is on the project row a Book View reads, so
// both report ProjectPolicy, as an import that names a cover does.

/// <summary>
/// Rewrites the project's title, book title and author; a null field is left as it is. The folder
/// name never changes: it was derived from the title at creation and is the project's id.
/// </summary>
public sealed record UpdateProjectMetadataMutation(
    ProjectFolderId FolderId, string? Title, string? BookTitle, string? Author) : ProjectStateMutation(FolderId);

/// <summary>
/// Names the project's cover image, or clears it with null.
/// <para>
/// Like <see cref="SetVoiceAudioMutation"/>, naming a cover never reports no-change: an upload
/// lands at its own file name, so uploading the same name again writes the same string over
/// different content. The image is written into the project folder before this commits, and the
/// one it replaces is removed after (ADR 0007).
/// </para>
/// </summary>
public sealed record SetCoverImageMutation(
    ProjectFolderId FolderId, string? CoverImageFileName) : ProjectStateMutation(FolderId)
{
    public override StateFileOwners WritesFiles => StateFileOwners.TheCover;
}

// ── manual and AI book edits ─────────────────────────────────────────────────
// What a person rewrites by hand, and what an approved AI edit program rewrites for them. The two
// halves are one family because they write the same two things — a node's title and an item's text
// — and an AI edit that reached item text by its own path is exactly how the stale-audio rule came
// to be closed in one corner and open in the other.
//
// A rewritten item's audio is discarded and any verdict on it deleted: the WAV speaks words the
// item no longer has, and because the item still *has* audio it is not Generatable, so a "select
// needs audio" pass skips it and the mismatch assembles into the exported m4b (ADR-0006). The
// review is deleted rather than dismissed — it judged audio that no longer exists, and Dismissed
// means "a human decided this was fine", which would wrongly suppress the verdict on whatever is
// generated next.
//
// A title is not on a Paragraph, so these report it as its own facet and let the reader rebuild.

/// <summary>Rewrites a Volume's title.</summary>
public sealed record UpdateVolumeTitleMutation(
    ProjectFolderId FolderId, Guid VolumeId, string Title) : ProjectStateMutation(FolderId);

/// <summary>Rewrites a Part's title.</summary>
public sealed record UpdatePartTitleMutation(
    ProjectFolderId FolderId, Guid PartId, string Title) : ProjectStateMutation(FolderId);

/// <summary>Rewrites a Chapter's title.</summary>
public sealed record UpdateChapterTitleMutation(
    ProjectFolderId FolderId, Guid ChapterId, string Title) : ProjectStateMutation(FolderId);

/// <summary>
/// Rewrites one ParagraphItem's text, discarding the audio that spoke the old words and the verdict
/// on it. Text that comes back unchanged keeps both.
/// </summary>
public sealed record UpdateParagraphItemTextMutation(
    ProjectFolderId FolderId, Guid ItemId, string? Text) : ProjectStateMutation(FolderId);

/// <summary>
/// Sets one speech item's delivery cue by hand: trimmed, blank meaning none, at most
/// <see cref="Implementations.SetItemDeliveryCueMutationImplementation.MaxLength"/> characters. A
/// changed cue discards the audio spoken in the old delivery and the verdict on it, as a text edit
/// does; a cue that comes back unchanged keeps both. Attribution's own cue writes go through
/// <see cref="AttributeParagraphItemsMutation"/> and keep the audio.
/// </summary>
public sealed record SetItemDeliveryCueMutation(
    ProjectFolderId FolderId, Guid ItemId, string? Cue) : ProjectStateMutation(FolderId);

/// <summary>
/// Applies an approved AI edit program as one Book mutation, however many titles and items it
/// rewrites. One commit rather than one per row is what keeps a reader from watching a Book edited
/// in front of them a row at a time, half of it under the old wording.
/// <para>
/// A target the Book no longer contains is skipped rather than refused: the program was planned
/// against a Book the user has been reviewing for minutes and may have edited since, and the rows
/// that still resolve are the ones they approved.
/// </para>
/// </summary>
public sealed record ApplyBookEditsMutation(
    ProjectFolderId FolderId, IReadOnlyList<BookEditItem> Edits) : ProjectStateMutation(FolderId);

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
    string? DesignPrompt = null) : ProjectStateMutation(FolderId);

/// <summary>Points the Character's default Voice Rule at this Voice.</summary>
public sealed record SetVoiceDefaultMutation(ProjectFolderId FolderId, Guid VoiceId) : ProjectStateMutation(FolderId);

/// <summary>Renames a Voice and rewrites its description. The audio it names is untouched.</summary>
public sealed record UpdateVoiceMutation(
    ProjectFolderId FolderId, Guid VoiceId, string VoiceName, string? Description) : ProjectStateMutation(FolderId);

/// <summary>Stores the description a generated Voice is synthesised from.</summary>
public sealed record SetVoiceDesignPromptMutation(
    ProjectFolderId FolderId, Guid VoiceId, string Prompt) : ProjectStateMutation(FolderId);

/// <summary>Overrides the voice-design server's settings for this Voice, or clears the override with null.</summary>
public sealed record SetVoiceDesignSettingsOverrideMutation(
    ProjectFolderId FolderId, Guid VoiceId, string? Json) : ProjectStateMutation(FolderId);

/// <summary>Overrides the TTS server's settings for this Voice, or clears the override with null.</summary>
public sealed record SetVoiceTtsSettingsOverrideMutation(
    ProjectFolderId FolderId, Guid VoiceId, string? Json) : ProjectStateMutation(FolderId);

/// <summary>Stores what the Voice's reference audio actually says — what a cloning TTS is given with it.</summary>
public sealed record SetVoiceTranscriptMutation(
    ProjectFolderId FolderId, Guid VoiceId, string Transcript) : ProjectStateMutation(FolderId);

/// <summary>
/// Points a Voice at reference audio.
/// <para>
/// Like <see cref="RecordParagraphItemAudioMutation"/> this never reports no-change: an upload lands
/// at a path derived from the Voice's id and name, so re-uploading writes the same string over
/// different audio. The path is a name, not the artifact.
/// </para>
/// </summary>
public sealed record SetVoiceAudioMutation(
    ProjectFolderId FolderId, Guid VoiceId, string AudioFileName) : ProjectStateMutation(FolderId)
{
    public override StateFileOwners WritesFiles => StateFileOwners.Voice(VoiceId);
}

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
    string DesignPrompt) : ProjectStateMutation(FolderId)
{
    public override StateFileOwners WritesFiles => StateFileOwners.Voice(VoiceId);
}

/// <summary>
/// Records that the voice audio editor rewrote a Voice's reference audio in place. The row does not
/// change — the edit lands on the path the Voice already names — but the audio a cloning TTS is
/// given does, so this never reports no-change. The edited WAV, and on a first edit the stored
/// original, are written before this commits and put back if it does not.
/// </summary>
public sealed record ApplyVoiceAudioEditMutation(ProjectFolderId FolderId, Guid VoiceId) : ProjectStateMutation(FolderId)
{
    public override StateFileOwners WritesFiles => StateFileOwners.Voice(VoiceId);
}

/// <summary>
/// Records that the voice audio editor put a Voice's original audio back. Like
/// <see cref="ApplyVoiceAudioEditMutation"/> it changes the audio and not the row. The stored
/// original is removed before this commits, so History records it gone with the step, and put
/// back if it doesn't.
/// </summary>
public sealed record RestoreVoiceAudioOriginalMutation(ProjectFolderId FolderId, Guid VoiceId) : ProjectStateMutation(FolderId)
{
    public override StateFileOwners WritesFiles => StateFileOwners.Voice(VoiceId);
}

/// <summary>
/// Switches a Voice between cloned-from-a-recording and designed-from-a-description. Turning a
/// Voice generated discards the recording it was cloned from — there is nothing left to clone — and
/// with it the stored original that would otherwise claim an edit on audio that no longer exists.
/// </summary>
public sealed record SetVoiceSourceMutation(
    ProjectFolderId FolderId, Guid VoiceId, bool IsGenerated) : ProjectStateMutation(FolderId);

/// <summary>
/// Removes a Voice, its audio, its stored original and every rule that named it. The Character's
/// default Voice Rule follows to its oldest remaining Voice, or goes too when that was the last one.
/// </summary>
public sealed record DeleteVoiceMutation(ProjectFolderId FolderId, Guid VoiceId) : ProjectStateMutation(FolderId);

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
    Guid? ToNodeId) : ProjectStateMutation(FolderId);

/// <summary>Removes one positional Voice Rule. The default rule is not one of these.</summary>
public sealed record DeleteVoiceRuleMutation(ProjectFolderId FolderId, Guid RuleId) : ProjectStateMutation(FolderId);

/// <summary>
/// Moves one positional Voice Rule past its neighbour. Rules are evaluated in order, so this is how
/// a reader decides which of two overlapping rules wins.
/// </summary>
public sealed record MoveVoiceRuleMutation(
    ProjectFolderId FolderId, Guid RuleId, RuleMoveDirection Direction) : ProjectStateMutation(FolderId);

// ── imports and rereads ──────────────────────────────────────────────────────
// Where a Book's content comes from in the first place: the initial import, the automatic reread of
// the source file, and the manual reread that re-splits it under hand-chosen options. All three are
// the same operation — replace what the Book holds with what the file says — so all three commit
// through the one mutation below.
//
// One mutation rather than "clear, then repopulate" is the whole point. Two commits would publish a
// receipt for the *empty* Book in between, and every other open Book View would rebuild against it:
// the reader would watch their project empty itself and refill. Inside one transaction there is no
// moment at which an empty or half-imported Book is visible to anyone.
//
// The Book's content is all that is replaced. Characters, Voices and Voice Rules survive a reread —
// they are what the reader built up around the text, not the text.

/// <summary>
/// Replaces the Book's content with freshly read content, in one commit.
/// <para>
/// <c>ReplaceExisting</c> is what separates a reread from a first import: a reread removes every
/// Volume, Part, Chapter, Paragraph and item first — and with them the attribution, audio and
/// reviews that hung off them — while an import into an empty Book adds to what is there.
/// </para>
/// <para>
/// <c>CoverImageFileName</c> names an image already written into the project folder. Like the Audio
/// Queue's take, the artifact exists before the Book names it (ADR 0007), and the producer removes
/// it again if this mutation does not commit. It is honoured only while the project has no cover:
/// an import never overwrites a cover the reader chose.
/// </para>
/// </summary>
public sealed record ImportBookContentMutation(
    ProjectFolderId FolderId,
    BookContent Content,
    bool ReplaceExisting,
    string? CoverImageFileName = null) : ProjectStateMutation(FolderId);
