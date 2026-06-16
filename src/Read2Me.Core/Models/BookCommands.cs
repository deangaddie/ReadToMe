using System;

namespace Read2Me.Core.Models;

public enum MergeDirection { Previous, Next }

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
public record SetParagraphCharacterCommand(ProjectFolderId FolderId, Guid ParagraphId, Guid CharacterId, string? VoiceInstructions = null) : BookCommand(FolderId);

// Title insertion
public record AddBookTitleCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record AddVolumeTitlesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record AddPartTitlesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record AddChapterTitlesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);

// Pause insertion
public record AddPausesCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
public record InsertPauseParagraphCommand(ProjectFolderId FolderId, Guid AnchorItemId, PauseInsertPosition Position, PauseKind PauseKind) : BookCommand(FolderId);

public enum PauseInsertPosition { Before, After }
public enum PauseKind { Pause, ParagraphPause, ChapterPause, PartPause, VolumePause }

// Clear
public record ClearBookContentCommand(ProjectFolderId FolderId) : BookCommand(FolderId);
