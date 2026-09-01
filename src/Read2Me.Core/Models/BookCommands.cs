namespace Read2Me.Core.Models;

public enum MergeDirection { Previous, Next }

/// <summary>
/// Where a new node lands relative to the anchor node it is inserted against.
/// Shared by every insertion command; the member names are also the JSON wire values.
/// </summary>
public enum InsertPosition { Before, After }

public abstract record BookCommand(ProjectFolderId FolderId);

// Delete
public record DeleteVolumeCommand(ProjectFolderId FolderId, Guid VolumeId) : BookCommand(FolderId);
public record DeletePartCommand(ProjectFolderId FolderId, Guid PartId) : BookCommand(FolderId);
public record DeleteChapterCommand(ProjectFolderId FolderId, Guid ChapterId) : BookCommand(FolderId);
public record DeleteParagraphCommand(ProjectFolderId FolderId, Guid ParagraphId) : BookCommand(FolderId);
public record DeleteParagraphItemCommand(ProjectFolderId FolderId, Guid ItemId) : BookCommand(FolderId);

// Update
public record UpdateVolumeTitleCommand(ProjectFolderId FolderId, Guid VolumeId, string Title) : BookCommand(FolderId);
public record UpdatePartTitleCommand(ProjectFolderId FolderId, Guid PartId, string Title) : BookCommand(FolderId);
public record UpdateChapterTitleCommand(ProjectFolderId FolderId, Guid ChapterId, string Title) : BookCommand(FolderId);
public record UpdateParagraphItemTextCommand(ProjectFolderId FolderId, Guid ItemId, string Text) : BookCommand(FolderId);

// Split — "split at X boundary to create new parent"
public record SplitAtPartCommand(ProjectFolderId FolderId, Guid PartId, string? NewVolumeTitle) : BookCommand(FolderId);
public record SplitAtChapterCommand(ProjectFolderId FolderId, Guid ChapterId, string? NewPartTitle) : BookCommand(FolderId);
public record SplitAtParagraphCommand(ProjectFolderId FolderId, Guid ParagraphId, string? NewChapterTitle) : BookCommand(FolderId);
public record SplitAtItemCommand(ProjectFolderId FolderId, Guid ItemId) : BookCommand(FolderId);

// Merge
public record MergeVolumeCommand(ProjectFolderId FolderId, Guid VolumeId, MergeDirection Direction) : BookCommand(FolderId);
public record MergePartCommand(ProjectFolderId FolderId, Guid PartId, MergeDirection Direction) : BookCommand(FolderId);
public record MergeChapterCommand(ProjectFolderId FolderId, Guid ChapterId, MergeDirection Direction) : BookCommand(FolderId);
public record MergeParagraphCommand(ProjectFolderId FolderId, Guid ParagraphId, MergeDirection Direction) : BookCommand(FolderId);
public record MergeParagraphItemCommand(ProjectFolderId FolderId, Guid ItemId, MergeDirection Direction) : BookCommand(FolderId);

// Character
public record SetItemCharacterCommand(ProjectFolderId FolderId, Guid ItemId, Guid? CharacterId) : BookCommand(FolderId);
public record CreateCharacterCommand(ProjectFolderId FolderId, string Name) : BookCommand(FolderId);
public record SetParagraphCharacterCommand(ProjectFolderId FolderId, Guid ParagraphId, Guid? CharacterId, string? VoiceInstructions = null) : BookCommand(FolderId);

/// <summary>
/// Stamps one speaker across every Character item in every listed paragraph — the bulk-assign
/// write, and the bulk sibling of <see cref="SetParagraphCharacterCommand"/>, which is left
/// unchanged and keeps its own callers.
/// </summary>
public record SetParagraphsCharacterCommand(ProjectFolderId FolderId, IReadOnlyList<Guid> ParagraphIds, Guid? CharacterId) : BookCommand(FolderId);

/// <summary>
/// One item's attribution, ready to apply: the item is addressed by id, and the speaker is already
/// resolved to a character id (null = unknown, which never erases an existing stamp).
/// <para>
/// Not to be confused with <c>AttributedItem</c>, the wire answer the LLM sends (index + speaker
/// name); this is the resolved apply-side record.
/// </para>
/// </summary>
public sealed record ItemAttribution(Guid ItemId, Guid? CharacterId, string? VoiceInstructions);

/// <summary>
/// Stamps speaker and voice instructions onto existing items of one paragraph. Item boundaries are
/// frozen (ADR 0005): this command never creates, deletes, reorders or retypes an item.
/// </summary>
public record AttributeItemsCommand(
    ProjectFolderId FolderId,
    Guid ParagraphId,
    IReadOnlyList<ItemAttribution> Items) : BookCommand(FolderId);

public record AddCharacterAliasCommand(ProjectFolderId FolderId, Guid CharacterId, string Name) : BookCommand(FolderId);
public record RemoveCharacterAliasCommand(ProjectFolderId FolderId, Guid AliasId) : BookCommand(FolderId);
public record MergeCharactersCommand(ProjectFolderId FolderId, Guid SurvivorId, Guid MergedId, bool AddNameAsAlias) : BookCommand(FolderId);
public record DeleteCharacterCommand(ProjectFolderId FolderId, Guid CharacterId) : BookCommand(FolderId);
public record RenameCharacterCommand(ProjectFolderId FolderId, Guid CharacterId, string Name) : BookCommand(FolderId);

// Narrator
/// <summary>
/// Points the book's narration at one of its Characters — Sherlock Holmes narrated by
/// Dr. Watson. <c>null</c> unlinks. The first project-scoped <see cref="BookCommand"/>:
/// every sibling addresses a node, character or voice.
/// </summary>
public record SetNarratorCharacterCommand(ProjectFolderId FolderId, Guid? CharacterId) : BookCommand(FolderId);

// Voice
public record CreateVoiceCommand(ProjectFolderId FolderId, Guid CharacterId, string Name, bool IsGenerated = false) : BookCommand(FolderId);
public record SetVoiceDefaultCommand(ProjectFolderId FolderId, Guid VoiceId) : BookCommand(FolderId);
public record UpdateVoiceCommand(ProjectFolderId FolderId, Guid VoiceId, string Name, string? Description) : BookCommand(FolderId);
public record SetVoiceDesignPromptCommand(ProjectFolderId FolderId, Guid VoiceId, string Prompt) : BookCommand(FolderId);
public record SetVoiceSettingsOverrideCommand(ProjectFolderId FolderId, Guid VoiceId, string? Json) : BookCommand(FolderId);
public record SetVoiceTtsSettingsOverrideCommand(ProjectFolderId FolderId, Guid VoiceId, string? Json) : BookCommand(FolderId);
public record SetVoiceTranscriptCommand(ProjectFolderId FolderId, Guid VoiceId, string Transcript) : BookCommand(FolderId);
public record SetVoiceAudioCommand(ProjectFolderId FolderId, Guid VoiceId, string AudioFileName) : BookCommand(FolderId);
public record SetVoiceGeneratedCommand(ProjectFolderId FolderId, Guid VoiceId, string AudioFileName, string Transcript, string DesignPrompt) : BookCommand(FolderId);
public record SetVoiceSourceCommand(ProjectFolderId FolderId, Guid VoiceId, bool IsGenerated) : BookCommand(FolderId);
public record DeleteVoiceCommand(ProjectFolderId FolderId, Guid VoiceId) : BookCommand(FolderId);

// Voice Rules
public enum RuleMoveDirection { Up, Down }
public record CreateVoiceRuleCommand(
    ProjectFolderId FolderId,
    Guid CharacterId,
    Guid VoiceId,
    VoiceAnchorLevel? FromLevel,
    Guid? FromNodeId,
    VoiceAnchorLevel? ToLevel,
    Guid? ToNodeId) : BookCommand(FolderId);
public record DeleteVoiceRuleCommand(ProjectFolderId FolderId, Guid RuleId) : BookCommand(FolderId);
public record MoveVoiceRuleCommand(ProjectFolderId FolderId, Guid RuleId, RuleMoveDirection Direction) : BookCommand(FolderId);

// Title insertion
public record AddBookTitleCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record AddVolumeTitlesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record AddPartTitlesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record AddChapterTitlesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);

// Item insertion
/// <summary>
/// Creates one Speech ParagraphItem next to <c>AnchorItemId</c>, inside the anchor's own Paragraph.
/// The new item is born unattributed — no speaker, no voice instructions, no audio: by construction
/// the anchor held two speakers, so inheriting its speaker would stamp a confident wrong answer that
/// looks attributed and never reaches the attribution queue.
/// </summary>
public record InsertParagraphItemCommand(ProjectFolderId FolderId, Guid AnchorItemId, InsertPosition Position, string Text) : BookCommand(FolderId);

// Pause insertion
public record AddPausesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record InsertPauseParagraphCommand(ProjectFolderId FolderId, Guid AnchorItemId, InsertPosition Position, PauseKind PauseKind) : BookCommand(FolderId);

public enum PauseKind { Pause, ParagraphPause, ChapterPause, PartPause, VolumePause }

// Clear
public record ClearBookContentCommand(ProjectFolderId FolderId) : BookCommand(FolderId);

// AI book edits
public enum BookEditTargetKind { VolumeTitle, PartTitle, ChapterTitle, ParagraphItemText }
public sealed record BookEditItem(BookEditTargetKind Kind, Guid Id, string NewValue);
public record ApplyBookEditsCommand(ProjectFolderId FolderId, IReadOnlyList<BookEditItem> Edits) : BookCommand(FolderId);

// Audio
public record SetParagraphItemAudioCommand(ProjectFolderId FolderId, Guid ItemId, string AudioFileName) : BookCommand(FolderId);

public enum AudioReviewState { NeedsReview, Dismissed }

public record SetAudioReviewCommand(
    ProjectFolderId FolderId,
    Guid ParagraphItemId,
    bool NormalizeOk,
    string? NormalizeReason,
    bool VerifyOk,
    double? Wer,
    string? VerifyReason,
    string? Transcript,
    string? OriginalTextSnapshot) : BookCommand(FolderId);

public record DismissAudioReviewCommand(ProjectFolderId FolderId, Guid ParagraphItemId) : BookCommand(FolderId);
