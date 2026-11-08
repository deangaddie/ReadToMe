using Read2Me.App.Api;
using Read2Me.App.Services.SetupCheck;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.History;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Live;

/// <summary>
/// Wire shapes for <c>/hubs/live</c>: one record per server→client family. A family with several event
/// kinds carries a <c>Kind</c> enum and nullable fields, so a client switches on one member name. The
/// wire form is the agent API's (ADR 0017): enums as member names, nulls sent. Every record here is a
/// component of the <c>v1</c> OpenAPI document (<see cref="LiveContractTransformer"/>), so the web client's
/// types are generated, not copied.
/// </summary>
public static class LiveGroups
{
    public const string Global = "global";
    public const string StreamLlm = "stream:llm";
    public const string StreamAudio = "stream:audio";
    public static string Project(string folder) => $"project:{folder}";
}

/// <summary>Both queues' roll-ups plus the attribution escalation banner (debounced, group <c>global</c>).</summary>
public sealed record QueueMessage(
    AttributionQueueSnapshot Attribution,
    AudioQueueSnapshot Audio,
    EscalationState? Escalation);

/// <summary>The latched "escalating N items → config (step S)" label; null when no escalation is active.</summary>
public sealed record EscalationState(int Step, string? ConfigName, int ItemCount);

/// <summary>
/// Per-node roll-up deltas for one project (debounced, group <c>project:{folder}</c>). A null value
/// means the node no longer rolls up (the folder was cleared or re-seeded without it).
/// </summary>
public sealed record NodeStatusMessage(
    string Folder,
    IReadOnlyDictionary<string, NodeStatusSummaryDto?> Nodes,
    int FolderAudioRemaining);

/// <summary>
/// Per-paragraph attribution and per-item audio status deltas for one project (debounced). A null entry
/// means the queue forgot the paragraph or item.
/// </summary>
public sealed record ItemStatusMessage(
    string Folder,
    IReadOnlyDictionary<string, ParagraphStatusEntry?> Paragraphs,
    IReadOnlyDictionary<string, ItemStatusEntry?> Items);

/// <summary>What the attribution queue currently says about one paragraph; null fields mean "nothing".</summary>
public sealed record ParagraphStatusEntry(ParagraphQueueStatus? Status, ParagraphOutcome? Outcome);

/// <summary>What the audio queue and the review mirror say about one item.</summary>
public sealed record ItemStatusEntry(
    AudioItemQueueStatus? Status,
    AudioItemOutcome? Outcome,
    long? AudioVersion,
    AudioReviewDto? Review);

/// <summary><see cref="BookMutationReceipt"/> as JSON with the folder flattened; <c>originId</c> untouched.</summary>
public sealed record ReceiptMessage(
    string Folder,
    string MutationName,
    Guid MutationId,
    long Revision,
    ReceiptEffects Effects,
    Guid OriginId)
{
    public static ReceiptMessage From(BookMutationReceipt r) =>
        new(r.FolderId.Value, r.MutationName, r.MutationId, r.Revision, ReceiptEffects.From(r.Effects), r.OriginId);
}

/// <summary>
/// <see cref="BookMutationEffects"/> on the wire, with <see cref="Facets"/> as a list of single facets
/// (never <c>None</c> or <c>All</c>): a <c>[Flags]</c> value would travel as one comma-joined string the
/// document can't describe.
/// </summary>
public sealed record ReceiptEffects(
    BookMutationScope Scope,
    IReadOnlyList<BookFacets> Facets,
    Guid? CreatedId,
    Guid? CreatedParentId,
    IReadOnlyList<Guid> NodeIds,
    IReadOnlyList<Guid> ParagraphIds,
    IReadOnlyList<Guid> ParagraphItemIds,
    IReadOnlyList<BookStructuralRelation> Structural,
    bool ChangedNothing)
{
    private static readonly BookFacets[] Single =
        [.. Enum.GetValues<BookFacets>().Where(f => f is not (BookFacets.None or BookFacets.All))];

    public static ReceiptEffects From(BookMutationEffects e) =>
        new(e.Scope, [.. Single.Where(f => e.Facets.HasFlag(f))], e.CreatedId, e.CreatedParentId,
            e.NodeIds, e.ParagraphIds, e.ParagraphItemIds, e.Structural, e.ChangedNothing);
}

public enum AssemblyMessageKind { PhaseStarted, Progress, Completed, Failed, Cancelled }

/// <summary>
/// <c>Folder</c> is the project the (single, global) run belongs to; <c>OutputFileName</c> rides on
/// <c>Completed</c>.
/// </summary>
public sealed record AssemblyMessage(
    AssemblyMessageKind Kind, AssemblyPhase? Phase = null, double? Fraction = null, string? Reason = null,
    string? Folder = null, string? OutputFileName = null);

/// <summary>Assembly service state for the connect-time snapshot.</summary>
public sealed record AssemblyState(
    bool IsRunning, AssemblyPhase? CurrentPhase, double EncodePercent, string? LastError, int AudioRemainingCount,
    string? Folder = null, string? OutputFileName = null);

public enum VoiceBatchMessageKind { Started, Progress, VoiceUpdated, Completed, Cancelled }

public sealed record VoiceBatchMessage(
    VoiceBatchMessageKind Kind,
    string? Operation = null,
    int? Processed = null,
    int? Total = null,
    int? Failed = null,
    string? CurrentVoiceName = null,
    Guid? CharacterId = null,
    Guid? VoiceId = null,
    string? DesignPrompt = null,
    string? AudioFileName = null,
    string? Transcript = null,
    double? ReferenceSeconds = null,
    string? ReferenceWarning = null);

/// <summary>Voice batch runner state for the connect-time snapshot.</summary>
public sealed record VoiceBatchState(
    bool IsRunning, int Processed, int Total, int Failed, string? CurrentVoiceName, string? CurrentOperation, string? LastError);

/// <summary>A watchdog transition; the snapshot keeps the last one per service.</summary>
public enum WatchdogKind { RecoveryStarted, ContainerRestarted, ServiceHealthy, ServiceDown }

public sealed record WatchdogMessage(WatchdogKind Kind, string Service, string? Reason = null);

/// <summary>
/// A managed service's status as last observed, pushed after every probe, lifecycle op and watchdog
/// transition so chips follow without polling (Angular ticket 25). <c>Op</c> with <c>Ok</c> /
/// <c>Error</c> is set only when a lifecycle op produced the observation — that is what a client toasts.
/// </summary>
public sealed record ServiceStatusMessage(
    string Name, AiServiceStatus Status, AiServiceOp? Op = null, bool? Ok = null, string? Error = null);

public enum PreflightMessageKind { Stage, Done }

/// <summary>Where one service of a pre-flight run is; <c>Pending</c> splits on what the row will do.</summary>
public enum PreflightStage { WaitingToStop, Stopping, Stopped, WaitingToStart, Starting, Ready, Failed }

/// <summary>
/// One pre-flight run (Angular ticket 25), sent to the connection that started it. <c>Stage</c>
/// carries a service and its stage; <c>Done</c> carries <c>Ok</c> and, on failure, the summary
/// <c>Reason</c>. <c>Run</c> is the id the 202 answered with.
/// </summary>
public sealed record PreflightMessage(
    PreflightMessageKind Kind,
    string Run,
    string? Name = null,
    PreflightStage? Stage = null,
    string? Error = null,
    bool? Ok = null,
    string? Reason = null);

public enum LlmMessageKind
{
    RunStarted, RunEnded, RequestStarted, Delta, StreamCompleted, StreamFailed, StreamAborted, EscalationStarted,
}

/// <summary><c>Delta</c> is a 100 ms batch of thinking + content text. Stream group only.</summary>
public sealed record LlmMessage(
    LlmMessageKind Kind,
    string? Thinking = null,
    string? Content = null,
    string? ParagraphPreview = null,
    string? Prompt = null,
    int? ConfigId = null,
    string? ConfigName = null,
    int? TokensIn = null,
    int? TokensOut = null,
    double? GenerationMs = null,
    double? TokensPerSecond = null,
    string? Reason = null,
    int? Step = null,
    int? ItemCount = null);

public enum AudioGenMessageKind
{
    ItemStarted, AudioGenerated, Normalized, PostProcessed, Transcribed, Verified, Failed, ModelLoading,
}

/// <summary>
/// One per <see cref="AudioGenEvent"/>. Stream group only. <c>ModelLoading</c> is not tied to an item:
/// its <c>Id</c> is empty and <c>Model</c> names the TTS model loading.
/// </summary>
public sealed record AudioGenMessage(
    AudioGenMessageKind Kind,
    Guid Id,
    int Attempt,
    string? Character = null,
    string? Text = null,
    bool? Ok = null,
    string? Reason = null,
    string? StepId = null,
    bool? Applied = null,
    string? Transcript = null,
    double? Wer = null,
    bool? Rescued = null,
    string? Model = null);

/// <summary>Which settings area changed, so other clients refresh their list.</summary>
public sealed record SettingsChangedMessage(string Area);

public enum BookEditMessageKind { Progress, Done, Failed, Dropped }

/// <summary>
/// One AI book-edit proposal run (Angular ticket 19), sent to the connection that started it.
/// <c>Program</c> is the session id the run belongs to; <c>Done</c> carries every row the run landed
/// (all of them, or the partial set a cancel kept). <c>Dropped</c>: a Restore of the project dropped
/// the session, with the <c>Reason</c> to show; sent to the project group, since the session outlives
/// its runs.
/// </summary>
public sealed record BookEditMessage(
    BookEditMessageKind Kind,
    string Program,
    int? Done = null,
    int? Total = null,
    bool? Cancelled = null,
    IReadOnlyList<BookEditRowDto>? Rows = null,
    string? Reason = null);

public enum LlmTestMessageKind { Done, Failed, Cancelled }

/// <summary>
/// How the LLM settings test send ended (Angular ticket 21), sent to the connection that started it.
/// The tokens themselves travel on <c>stream:llm</c>.
/// </summary>
public sealed record LlmTestMessage(LlmTestMessageKind Kind, int ConfigId, string? Reason = null);

public enum HistoryMessageKind { StepLanded, StepChanged, StepClosed, BusyChanged, Restoring }

/// <summary>
/// <c>StepLanded</c> | <c>StepChanged</c> | <c>StepClosed</c>: a step that appeared in a project's
/// History, an open one a later commit extended, or one that closed, with its row as
/// <c>GET /api/projects/{folder}/history</c> lists it. Sent to the project group. A step recorded by
/// one mutation lands and closes together, so both arrive back to back.
/// <c>BusyChanged</c> carries no step: the project's busy reason ("Attribution: 128 paragraphs left")
/// moved, or went null when the project went idle. A busy project can't be viewed at a step.
/// <c>Restoring</c>: a Restore of the project <c>Phase</c> started or finished; <c>Finished</c> carries
/// the Restore step it added, or no step when it added none or failed. Writes are refused in between.
/// </summary>
public sealed record HistoryMessage(
    HistoryMessageKind Kind, string Folder, HistoryStep? Step = null, string? Reason = null, RestoreEventKind? Phase = null);

/// <summary>
/// <see cref="ProposedEdit"/> on the wire; enums as member names. Shared with the REST reads of a
/// proposal run (<c>BookEditEndpoints</c>) on purpose: a row a client got pushed and a row it read
/// back after missing the push have to be the same shape, or a review screen would need two.
/// <paramref name="NewValue"/> is null on a <c>Failed</c> row.
/// <paramref name="Sources"/> are in the order the plan names them (empty when it reads none; a missing
/// source is left out, the reason names it). <paramref name="CanAskAgain"/> is false on a missing- or
/// self-source Failed row, where asking again would only return the same failure.
/// </summary>
public sealed record BookEditRowDto(
    BookEditTargetKind Kind, Guid Id, string DisplayPath, string OldValue, string? NewValue, ProposalStatus Status, string? FailureReason,
    IReadOnlyList<BookEditSourceDto> Sources, bool CanAskAgain)
{
    public static BookEditRowDto From(ProposedEdit p) =>
        new(p.Kind, p.Id, p.DisplayPath, p.OldValue, p.NewValue, p.Status, p.FailureReason,
            [.. p.Sources.Select(s => new BookEditSourceDto(s.Name, s.Label, s.Value))], p.CanAskAgain);
}

/// <summary>One Edit source value a row read: its name (<c>p1</c>), the plain label (<c>Paragraph 1</c>) and the value.</summary>
public sealed record BookEditSourceDto(string Name, string Label, string Value);

public enum SetupCheckMessageKind { Progress, Done }

/// <summary>
/// The Setup check's Full check, sent to every client (group <c>global</c>) so the page, the app-bar
/// shield and every tab read one source. Each carries the whole report: the rows (the current step's
/// are Checking) and <c>FullRun</c>; <c>Done</c> carries the final one.
/// </summary>
public sealed record SetupCheckMessage(SetupCheckMessageKind Kind, SetupCheckReport Report);

/// <summary>
/// Everything a project group member needs on join: current revision plus full status maps. Answers
/// <c>JoinProject</c>; also an entry of <c>LiveSnapshot.projects</c>.
/// </summary>
public sealed record ProjectSnapshot(
    string Folder,
    long Revision,
    IReadOnlyDictionary<string, NodeStatusSummaryDto?> Nodes,
    int FolderAudioRemaining,
    IReadOnlyDictionary<string, ParagraphStatusEntry?> Paragraphs,
    IReadOnlyDictionary<string, ItemStatusEntry?> Items);

/// <summary>Answer to <c>GetSnapshot</c>: singleton state plus one <see cref="ProjectSnapshot"/> per joined project.</summary>
public sealed record LiveSnapshot(
    QueueMessage Queue,
    AssemblyState Assembly,
    VoiceBatchState VoiceBatch,
    IReadOnlyDictionary<string, WatchdogKind> Watchdog,
    IReadOnlyDictionary<string, AiServiceStatus> ServiceStatus,
    ThroughputSnapshot Throughput,
    IReadOnlyDictionary<string, ProjectSnapshot> Projects);

/// <summary>
/// A series registry changed, or the series was deleted (#213): a signal to read <c>/api/series</c> again,
/// with no payload, debounced 250 ms per series, sent to every client (group <c>global</c>). A delete inside
/// the window wins over the changes before it.
/// </summary>
public sealed record SeriesMessage(Guid SeriesId, SeriesChangeKind Kind);
