using Read2Me.Core.Models;

namespace Read2Me.Services.History;

/// <summary>
/// One <b>History step</b> as History lists it (context/history.md). <see cref="Sequence"/> is for
/// display only: the line of steps is the <see cref="ParentStepId"/> chain from the Head, so nothing
/// assumes the highest sequence is the tip (ADR 0016).
/// <para>
/// <see cref="Note"/> says what is unusual about the step, or is null: "part 2" when another commit
/// split its scope, how a Background run ended early ("212 of 340 paragraphs, cancelled"), or
/// "interrupted" when the app stopped while it was open — joined by " · ", without brackets.
/// </para>
/// <para>
/// <see cref="EndedAt"/> is when the step closed, or, while it is open, its last change. An
/// interrupted step keeps the time of its last change: when it really ended is not known.
/// <see cref="IsOpen"/>, not <see cref="EndedAt"/>, says whether it is open.
/// </para>
/// <para>
/// <see cref="RestoredFromStepId"/> is the step a Restore step brought back; null for any other.
/// <see cref="CantOpen"/> marks a step that can no longer be opened (its old schema fails to migrate).
/// </para>
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
    bool IsOpen,
    string? Note,
    Guid? RestoredFromStepId = null,
    bool CantOpen = false);

/// <summary>A project's History: its steps newest first, and the step the Head points at (null before the first).</summary>
public sealed record HistoryList(IReadOnlyList<HistoryStep> Steps, Guid? HeadStepId);

public enum HistoryStepEventKind { Landed, Changed, Closed }

/// <summary>
/// A step that appeared in a project's History, an open one a later commit extended (its label,
/// "what changed" and last change move on), or one that closed, with its row as it now stands.
/// </summary>
public sealed record HistoryStepEvent(ProjectFolderId FolderId, HistoryStepEventKind Kind, HistoryStep Step);
