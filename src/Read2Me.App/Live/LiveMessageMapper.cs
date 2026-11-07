using Read2Me.App.Characters;
using Read2Me.App.Services.SetupCheck;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Health;
using Read2Me.Services.History;
using Read2Me.Services.Llm;

namespace Read2Me.App.Live;

/// <summary>In-process event records → wire records. Pure; shared by the relay (live) and the hub (replay).</summary>
public static class LiveMessageMapper
{
    /// <summary>Control events only; deltas are batched by <see cref="DeltaBatcher"/> and return null here.</summary>
    public static LlmMessage? Control(LlmStreamEvent e) => e switch
    {
        RunStarted => new LlmMessage(LlmMessageKind.RunStarted),
        RunEnded => new LlmMessage(LlmMessageKind.RunEnded),
        RequestStarted r => new LlmMessage(LlmMessageKind.RequestStarted,
            ParagraphPreview: r.ParagraphPreview, Prompt: r.Prompt, ConfigId: r.ConfigId, ConfigName: r.ConfigName),
        StreamCompleted c => new LlmMessage(LlmMessageKind.StreamCompleted,
            TokensIn: c.TokensIn, TokensOut: c.TokensOut, GenerationMs: c.GenerationMs, TokensPerSecond: c.TokensPerSecond),
        StreamFailed f => new LlmMessage(LlmMessageKind.StreamFailed, Reason: f.Reason),
        StreamAborted a => new LlmMessage(LlmMessageKind.StreamAborted,
            TokensOut: a.TokensOut, GenerationMs: a.GenerationMs, TokensPerSecond: a.TokensPerSecond),
        EscalationStarted es => new LlmMessage(LlmMessageKind.EscalationStarted,
            Step: es.Step, ConfigName: es.ConfigName, ItemCount: es.ItemCount),
        _ => null,
    };

    public static LlmMessage Delta(string thinking, string content) =>
        new(LlmMessageKind.Delta, Thinking: thinking, Content: content);

    /// <summary>
    /// A journal replay as messages: each run of consecutive deltas collapses into one
    /// <c>Delta</c>, control events pass through in order.
    /// </summary>
    public static IEnumerable<LlmMessage> Replay(IEnumerable<LlmStreamEvent> events)
    {
        var thinking = new System.Text.StringBuilder();
        var content = new System.Text.StringBuilder();
        foreach (var e in events)
        {
            switch (e)
            {
                case ThinkingDelta t:
                    thinking.Append(t.Text);
                    continue;
                case ContentDelta c:
                    content.Append(c.Text);
                    continue;
            }
            if (thinking.Length > 0 || content.Length > 0)
            {
                yield return Delta(thinking.ToString(), content.ToString());
                thinking.Clear();
                content.Clear();
            }
            if (Control(e) is { } control) yield return control;
        }
        if (thinking.Length > 0 || content.Length > 0)
            yield return Delta(thinking.ToString(), content.ToString());
    }

    public static AudioGenMessage Map(AudioGenEvent e) => e switch
    {
        ItemStarted s => new AudioGenMessage(AudioGenMessageKind.ItemStarted, s.Id, s.Attempt, Character: s.Character, Text: s.Text),
        AudioGenerated g => new AudioGenMessage(AudioGenMessageKind.AudioGenerated, g.Id, g.Attempt),
        Normalized n => new AudioGenMessage(AudioGenMessageKind.Normalized, n.Id, n.Attempt, Ok: n.Ok, Reason: n.Reason),
        PostProcessed p => new AudioGenMessage(AudioGenMessageKind.PostProcessed, p.Id, p.Attempt, Reason: p.Reason, StepId: p.StepId, Applied: p.Applied),
        Transcribed t => new AudioGenMessage(AudioGenMessageKind.Transcribed, t.Id, t.Attempt, Transcript: t.Transcript),
        Verified v => new AudioGenMessage(AudioGenMessageKind.Verified, v.Id, v.Attempt, Ok: v.Ok, Reason: v.Reason, Wer: v.Wer, Rescued: v.Rescued),
        Failed f => new AudioGenMessage(AudioGenMessageKind.Failed, f.Id, f.Attempt, Reason: f.Reason),
        // Not tied to an item: the TTS gate sits below the item pipeline. Id stays empty.
        TtsModelLoading m => new AudioGenMessage(AudioGenMessageKind.ModelLoading, Guid.Empty, 0, Model: m.ModelId),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped AudioGenEvent"),
    };

    public static AssemblyMessage Map(AssemblyEvent e) => e switch
    {
        AssemblyPhaseStarted p => new AssemblyMessage(AssemblyMessageKind.PhaseStarted, Phase: p.Phase, Folder: e.Folder),
        AssemblyEncodeProgress p => new AssemblyMessage(AssemblyMessageKind.Progress, Fraction: p.Fraction, Folder: e.Folder),
        AssemblyCompleted c => new AssemblyMessage(AssemblyMessageKind.Completed, Folder: e.Folder, OutputFileName: c.OutputFileName),
        AssemblyFailed f => new AssemblyMessage(AssemblyMessageKind.Failed, Reason: f.Reason, Folder: e.Folder),
        AssemblyCancelled => new AssemblyMessage(AssemblyMessageKind.Cancelled, Folder: e.Folder),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped AssemblyEvent"),
    };

    public static VoiceBatchMessage Map(VoiceBatchEvent e) => e switch
    {
        BatchStarted s => new VoiceBatchMessage(VoiceBatchMessageKind.Started, Operation: s.Operation, Total: s.Total),
        BatchProgress p => new VoiceBatchMessage(VoiceBatchMessageKind.Progress,
            Processed: p.Processed, Total: p.Total, Failed: p.Failed, CurrentVoiceName: p.CurrentVoiceName),
        VoiceUpdated v => new VoiceBatchMessage(VoiceBatchMessageKind.VoiceUpdated,
            CharacterId: v.CharacterId, VoiceId: v.VoiceId, DesignPrompt: v.DesignPrompt,
            AudioFileName: v.AudioFileName, Transcript: v.Transcript,
            ReferenceSeconds: v.ReferenceSeconds, ReferenceWarning: v.ReferenceWarning),
        BatchCompleted c => new VoiceBatchMessage(VoiceBatchMessageKind.Completed, Processed: c.Processed, Failed: c.Failed),
        BatchCancelled => new VoiceBatchMessage(VoiceBatchMessageKind.Cancelled),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped VoiceBatchEvent"),
    };

    public static WatchdogMessage Map(WatchdogEvent e) => e switch
    {
        RecoveryStarted r => new WatchdogMessage(WatchdogKind.RecoveryStarted, r.Service, r.Reason),
        ContainerRestarted c => new WatchdogMessage(WatchdogKind.ContainerRestarted, c.Service),
        ServiceHealthy h => new WatchdogMessage(WatchdogKind.ServiceHealthy, h.Service),
        ServiceDown d => new WatchdogMessage(WatchdogKind.ServiceDown, d.Service, d.LastError),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped WatchdogEvent"),
    };

    // The bookEdit family has no in-process event record: the run coordinator builds each message
    // as it goes. They are minted here all the same, beside every other family.
    public static BookEditMessage BookEditProgress(string program, int done, int total) =>
        new BookEditMessage(BookEditMessageKind.Progress, program, Done: done, Total: total);

    public static BookEditMessage BookEditDone(string program, IReadOnlyList<ProposedEdit> rows, int total, bool cancelled) =>
        new BookEditMessage(BookEditMessageKind.Done, program, Done: rows.Count, Total: total, Cancelled: cancelled,
            Rows: [.. rows.Select(BookEditRowDto.From)]);

    public static BookEditMessage BookEditFailed(string program, string reason) =>
        new BookEditMessage(BookEditMessageKind.Failed, program, Reason: reason);

    public static ServiceStatusMessage Map(ServiceStatusChanged c) =>
        new ServiceStatusMessage(c.Service, c.Status, c.Op, c.Ok, c.Error);

    /// <summary>
    /// The status a probe would report while the watchdog holds this opinion — the same mapping
    /// <c>AiServiceControl.GetStatusAsync</c> applies before it looks at the container.
    /// </summary>
    public static ServiceStatusMessage ImpliedStatus(WatchdogEvent e) => e switch
    {
        RecoveryStarted r => new ServiceStatusMessage(r.Service, AiServiceStatus.Recovering),
        ContainerRestarted c => new ServiceStatusMessage(c.Service, AiServiceStatus.Recovering),
        ServiceHealthy h => new ServiceStatusMessage(h.Service, AiServiceStatus.Ready),
        ServiceDown d => new ServiceStatusMessage(d.Service, AiServiceStatus.Down),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped WatchdogEvent"),
    };

    public static PreflightMessage PreflightStage(string run, string name, PreflightStage stage, string? error = null) =>
        new PreflightMessage(PreflightMessageKind.Stage, run, Name: name, Stage: stage, Error: error);

    public static PreflightMessage PreflightDone(string run, bool ok, string? reason = null) =>
        new PreflightMessage(PreflightMessageKind.Done, run, Ok: ok, Reason: reason);

    public static SetupCheckMessage SetupCheckProgress(SetupCheckReport report) => new SetupCheckMessage(SetupCheckMessageKind.Progress, report);

    public static SetupCheckMessage SetupCheckDone(SetupCheckReport report) => new SetupCheckMessage(SetupCheckMessageKind.Done, report);

    public static LlmTestMessage LlmTestDone(int configId) => new LlmTestMessage(LlmTestMessageKind.Done, configId);

    public static LlmTestMessage LlmTestFailed(int configId, string reason) =>
        new LlmTestMessage(LlmTestMessageKind.Failed, configId, reason);

    public static LlmTestMessage LlmTestCancelled(int configId) => new LlmTestMessage(LlmTestMessageKind.Cancelled, configId);

    public static HistoryMessage Map(HistoryStepEvent e) => e.Kind switch
    {
        HistoryStepEventKind.Landed => new HistoryMessage(HistoryMessageKind.StepLanded, e.FolderId.Value, e.Step),
        HistoryStepEventKind.Changed => new HistoryMessage(HistoryMessageKind.StepChanged, e.FolderId.Value, e.Step),
        HistoryStepEventKind.Closed => new HistoryMessage(HistoryMessageKind.StepClosed, e.FolderId.Value, e.Step),
        _ => throw new NotSupportedException($"Unhandled History step event {e.Kind}."),
    };

    /// <summary>The project's busy reason moved; null when it went idle.</summary>
    public static HistoryMessage BusyChanged(string folder, string? reason) =>
        new HistoryMessage(HistoryMessageKind.BusyChanged, folder, Reason: reason);

    /// <summary>A Restore started, or finished with the step it added.</summary>
    public static HistoryMessage Restoring(RestoreEvent e) =>
        new HistoryMessage(HistoryMessageKind.Restoring, e.FolderId.Value, e.Step, Phase: e.Kind);

    /// <summary>Why a dropped Edit with AI session is gone: what its open dialog says.</summary>
    public const string RestoredReason = "The project was restored — run it again";

    public static BookEditMessage BookEditDropped(string program) => new BookEditMessage(BookEditMessageKind.Dropped, program, Reason: RestoredReason);
}
