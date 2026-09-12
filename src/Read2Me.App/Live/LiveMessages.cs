using Read2Me.Services.Audio;
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

/// <summary><c>kind</c>: phaseStarted | progress | completed | failed | cancelled.</summary>
public sealed record AssemblyMessage(string Kind, string? Phase = null, double? Fraction = null, string? Reason = null);

/// <summary>Assembly service state for the connect-time snapshot.</summary>
public sealed record AssemblyState(bool IsRunning, string? CurrentPhase, double EncodePercent, string? LastError, int AudioRemainingCount);

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
    ThroughputSnapshot Throughput,
    IReadOnlyDictionary<string, ProjectSnapshot> Projects);
