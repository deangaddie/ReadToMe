using Read2Me.Core.Models;

namespace Read2Me.Services.History;

/// <summary>
/// One <b>History step</b> as History lists it (context/history.md). <see cref="Sequence"/> is for
/// display only: the line of steps is the <see cref="ParentStepId"/> chain from the Head, so nothing
/// assumes the highest sequence is the tip (ADR 0016).
/// </summary>
public sealed record HistoryStep(
    Guid StepId,
    Guid? ParentStepId,
    int Sequence,
    string Label,
    string? Summary,
    string Author,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    bool IsOpen);

/// <summary>A project's History: its steps newest first, and the step the Head points at (null before the first).</summary>
public sealed record HistoryList(IReadOnlyList<HistoryStep> Steps, Guid? HeadStepId);

public enum HistoryStepEventKind { Landed, Closed }

/// <summary>A step that appeared in a project's History, or one that closed, with its row as it now stands.</summary>
public sealed record HistoryStepEvent(ProjectFolderId FolderId, HistoryStepEventKind Kind, HistoryStep Step);
