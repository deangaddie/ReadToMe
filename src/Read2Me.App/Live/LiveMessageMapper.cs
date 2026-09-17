using Read2Me.App.Characters;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;

namespace Read2Me.App.Live;

/// <summary>In-process event records → wire records. Pure; shared by the relay (live) and the hub (replay).</summary>
public static class LiveMessageMapper
{
    /// <summary>Control events only; deltas are batched by <see cref="DeltaBatcher"/> and return null here.</summary>
    public static LlmMessage? Control(LlmStreamEvent e) => e switch
    {
        RunStarted => new LlmMessage("runStarted"),
        RunEnded => new LlmMessage("runEnded"),
        RequestStarted r => new LlmMessage("requestStarted",
            ParagraphPreview: r.ParagraphPreview, Prompt: r.Prompt, ConfigId: r.ConfigId, ConfigName: r.ConfigName),
        StreamCompleted c => new LlmMessage("streamCompleted",
            TokensIn: c.TokensIn, TokensOut: c.TokensOut, GenerationMs: c.GenerationMs, TokensPerSecond: c.TokensPerSecond),
        StreamFailed f => new LlmMessage("streamFailed", Reason: f.Reason),
        StreamAborted a => new LlmMessage("streamAborted",
            TokensOut: a.TokensOut, GenerationMs: a.GenerationMs, TokensPerSecond: a.TokensPerSecond),
        EscalationStarted es => new LlmMessage("escalationStarted",
            Step: es.Step, ConfigName: es.ConfigName, ItemCount: es.ItemCount),
        _ => null,
    };

    public static LlmMessage Delta(string thinking, string content) =>
        new("delta", Thinking: thinking, Content: content);

    /// <summary>
    /// A journal replay as messages: each run of consecutive deltas collapses into one
    /// <c>delta</c>, control events pass through in order.
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
        ItemStarted s => new AudioGenMessage("itemStarted", s.Id, s.Attempt, Character: s.Character, Text: s.Text),
        AudioGenerated g => new AudioGenMessage("audioGenerated", g.Id, g.Attempt),
        Normalized n => new AudioGenMessage("normalized", n.Id, n.Attempt, Ok: n.Ok, Reason: n.Reason),
        PostProcessed p => new AudioGenMessage("postProcessed", p.Id, p.Attempt, Reason: p.Reason, StepId: p.StepId, Applied: p.Applied),
        Transcribed t => new AudioGenMessage("transcribed", t.Id, t.Attempt, Transcript: t.Transcript),
        Verified v => new AudioGenMessage("verified", v.Id, v.Attempt, Ok: v.Ok, Reason: v.Reason, Wer: v.Wer, Rescued: v.Rescued),
        Failed f => new AudioGenMessage("failed", f.Id, f.Attempt, Reason: f.Reason),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped AudioGenEvent"),
    };

    public static AssemblyMessage Map(AssemblyEvent e) => e switch
    {
        AssemblyPhaseStarted p => new AssemblyMessage("phaseStarted", Phase: p.Phase.ToString()),
        AssemblyEncodeProgress p => new AssemblyMessage("progress", Fraction: p.Fraction),
        AssemblyCompleted => new AssemblyMessage("completed"),
        AssemblyFailed f => new AssemblyMessage("failed", Reason: f.Reason),
        AssemblyCancelled => new AssemblyMessage("cancelled"),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped AssemblyEvent"),
    };

    public static VoiceBatchMessage Map(VoiceBatchEvent e) => e switch
    {
        BatchStarted s => new VoiceBatchMessage("started", Operation: s.Operation, Total: s.Total),
        BatchProgress p => new VoiceBatchMessage("progress",
            Processed: p.Processed, Total: p.Total, Failed: p.Failed, CurrentVoiceName: p.CurrentVoiceName),
        VoiceUpdated v => new VoiceBatchMessage("voiceUpdated",
            CharacterId: v.CharacterId, VoiceId: v.VoiceId, DesignPrompt: v.DesignPrompt,
            AudioFileName: v.AudioFileName, Transcript: v.Transcript),
        BatchCompleted c => new VoiceBatchMessage("completed", Processed: c.Processed, Failed: c.Failed),
        BatchCancelled => new VoiceBatchMessage("cancelled"),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped VoiceBatchEvent"),
    };

    public static WatchdogMessage Map(WatchdogEvent e) => e switch
    {
        RecoveryStarted r => new WatchdogMessage("recoveryStarted", r.Service, r.Reason),
        ContainerRestarted c => new WatchdogMessage("containerRestarted", c.Service),
        ServiceHealthy h => new WatchdogMessage("serviceHealthy", h.Service),
        ServiceDown d => new WatchdogMessage("serviceDown", d.Service, d.LastError),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.GetType().Name, "Unmapped WatchdogEvent"),
    };

    // The bookEdit family has no in-process event record: the run coordinator builds each message
    // as it goes. They are minted here all the same so the TS mirror's contract test sees the kinds.
    public static BookEditMessage BookEditProgress(string program, int done, int total) =>
        new BookEditMessage("progress", program, Done: done, Total: total);

    public static BookEditMessage BookEditDone(string program, IReadOnlyList<ProposedEdit> rows, int total, bool cancelled) =>
        new BookEditMessage("done", program, Done: rows.Count, Total: total, Cancelled: cancelled,
            Rows: [.. rows.Select(BookEditRowDto.From)]);

    public static BookEditMessage BookEditFailed(string program, string reason) =>
        new BookEditMessage("failed", program, Reason: reason);
}
