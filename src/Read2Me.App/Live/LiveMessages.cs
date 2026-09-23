using Read2Me.Services.Audio;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.Live;

/// <summary>
/// Wire shapes for <c>/hubs/live</c> (Angular ticket 06; mirrored in TypeScript by ticket 07). One
/// record per server→client family; a family with several event kinds carries a <c>kind</c>
/// discriminator and nullable fields so a client can switch on one string. Enums travel as their
/// member names, nulls are omitted (see <see cref="LiveServiceCollectionExtensions"/>).
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
    QueueSnapshot Attribution,
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
    IReadOnlyDictionary<string, NodeStatusSummary?> Nodes,
    int FolderAudioRemaining);

/// <summary>Per-paragraph attribution and per-item audio status deltas for one project (debounced).</summary>
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
    AudioReviewInfo? Review);

/// <summary><see cref="BookMutationReceipt"/> as JSON with the folder flattened; <c>originId</c> untouched.</summary>
public sealed record ReceiptMessage(
    string Folder,
    string MutationName,
    Guid MutationId,
    long Revision,
    BookMutationEffects Effects,
    Guid OriginId)
{
    public static ReceiptMessage From(BookMutationReceipt r) =>
        new(r.FolderId.Value, r.MutationName, r.MutationId, r.Revision, r.Effects, r.OriginId);
}

/// <summary>
/// <c>kind</c>: phaseStarted | progress | completed | failed | cancelled. <c>Folder</c> is the project the
/// (single, global) run belongs to; <c>OutputFileName</c> rides on <c>completed</c>.
/// </summary>
public sealed record AssemblyMessage(
    string Kind, string? Phase = null, double? Fraction = null, string? Reason = null,
    string? Folder = null, string? OutputFileName = null);

/// <summary>Assembly service state for the connect-time snapshot.</summary>
public sealed record AssemblyState(
    bool IsRunning, string? CurrentPhase, double EncodePercent, string? LastError, int AudioRemainingCount,
    string? Folder = null, string? OutputFileName = null);

/// <summary><c>kind</c>: started | progress | voiceUpdated | completed | cancelled.</summary>
public sealed record VoiceBatchMessage(
    string Kind,
    string? Operation = null,
    int? Processed = null,
    int? Total = null,
    int? Failed = null,
    string? CurrentVoiceName = null,
    Guid? CharacterId = null,
    Guid? VoiceId = null,
    string? DesignPrompt = null,
    string? AudioFileName = null,
    string? Transcript = null);

/// <summary>Voice batch runner state for the connect-time snapshot.</summary>
public sealed record VoiceBatchState(
    bool IsRunning, int Processed, int Total, int Failed, string? CurrentVoiceName, string? CurrentOperation, string? LastError);

/// <summary><c>kind</c>: recoveryStarted | containerRestarted | serviceHealthy | serviceDown.</summary>
public sealed record WatchdogMessage(string Kind, string Service, string? Reason = null);

/// <summary>
/// A managed service's status as last observed (<c>AiServiceStatus</c> member name), pushed after
/// every probe, lifecycle op and watchdog transition so chips follow without polling (Angular
/// ticket 25). <c>op</c> (start | restart | shutdown) with <c>ok</c> / <c>error</c> is present only
/// when a lifecycle op produced the observation — that is what a client toasts.
/// </summary>
public sealed record ServiceStatusMessage(
    string Name, string Status, string? Op = null, bool? Ok = null, string? Error = null);

/// <summary>
/// <c>kind</c>: stage | done — one pre-flight run (Angular ticket 25), sent to the connection that
/// started it. <c>stage</c> carries a service and its stage (waitingToStop | stopping | stopped |
/// waitingToStart | starting | ready | failed); <c>done</c> carries <c>ok</c> and, on failure, the
/// summary <c>reason</c>. <c>run</c> is the id the 202 answered with.
/// </summary>
public sealed record PreflightMessage(
    string Kind,
    string Run,
    string? Name = null,
    string? Stage = null,
    string? Error = null,
    bool? Ok = null,
    string? Reason = null);

/// <summary>
/// <c>kind</c>: runStarted | runEnded | requestStarted | delta | streamCompleted | streamFailed |
/// streamAborted | escalationStarted. <c>delta</c> is a 100 ms batch of thinking + content text.
/// Stream group only.
/// </summary>
public sealed record LlmMessage(
    string Kind,
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

/// <summary>
/// <c>kind</c>: itemStarted | audioGenerated | normalized | postProcessed | transcribed | verified |
/// failed — one per <see cref="AudioGenEvent"/>. Stream group only.
/// </summary>
public sealed record AudioGenMessage(
    string Kind,
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
    bool? Rescued = null);

/// <summary>Which settings area changed, so other clients refresh their list.</summary>
public sealed record SettingsChangedMessage(string Area);

/// <summary>
/// <c>kind</c>: progress | done | failed — one AI book-edit proposal run (Angular ticket 19), sent
/// to the connection that started it. <c>program</c> is the session id the run belongs to;
/// <c>done</c> carries every row the run landed (all of them, or the partial set a cancel kept).
/// </summary>
public sealed record BookEditMessage(
    string Kind,
    string Program,
    int? Done = null,
    int? Total = null,
    bool? Cancelled = null,
    IReadOnlyList<BookEditRowDto>? Rows = null,
    string? Reason = null);

/// <summary>
/// <c>kind</c>: done | failed | cancelled — how the LLM settings test send ended (Angular ticket 21),
/// sent to the connection that started it. The tokens themselves travel on <c>stream:llm</c>.
/// </summary>
public sealed record LlmTestMessage(string Kind, int ConfigId, string? Reason = null);

/// <summary>
/// <see cref="ProposedEdit"/> on the wire; enums as member names. Shared with the REST reads of a
/// proposal run (<c>BookEditEndpoints</c>) on purpose: a row a client got pushed and a row it read
/// back after missing the push have to be the same shape, or a review screen would need two.
/// </summary>
public sealed record BookEditRowDto(
    string Kind, Guid Id, string DisplayPath, string OldValue, string? NewValue, string Status, string? FailureReason)
{
    public static BookEditRowDto From(ProposedEdit p) =>
        new(p.Kind.ToString(), p.Id, p.DisplayPath, p.OldValue, p.NewValue, p.Status.ToString(), p.FailureReason);
}

/// <summary>Everything a project group member needs on join: current revision plus full status maps.</summary>
public sealed record ProjectSnapshot(
    string Folder,
    long Revision,
    IReadOnlyDictionary<string, NodeStatusSummary?> Nodes,
    int FolderAudioRemaining,
    IReadOnlyDictionary<string, ParagraphStatusEntry?> Paragraphs,
    IReadOnlyDictionary<string, ItemStatusEntry?> Items);

/// <summary>Answer to <c>GetSnapshot</c>: singleton state plus one <see cref="ProjectSnapshot"/> per joined project.</summary>
public sealed record LiveSnapshot(
    QueueMessage Queue,
    AssemblyState Assembly,
    VoiceBatchState VoiceBatch,
    IReadOnlyDictionary<string, string> Watchdog,
    IReadOnlyDictionary<string, string> ServiceStatus,
    ThroughputSnapshot Throughput,
    IReadOnlyDictionary<string, ProjectSnapshot> Projects);
