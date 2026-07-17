namespace Read2Me.Services.Llm
{
    public abstract record LlmStreamEvent;

    /// <summary>
    /// Opens a Throughput Run — the unit a "total" tok/s is measured over: one attribution
    /// queue, one voice-design batch, one settings test send, one voice design, one discovery
    /// call. Published by whichever producer owns the work; a batch of N is <em>one</em> run and
    /// a single request is a genuine run of one. Run boundaries are explicit rather than
    /// inferred from a quiet period, so a heuristic never decides the numbers (ADR 0003).
    /// </summary>
    public sealed record RunStarted : LlmStreamEvent;

    /// <summary>
    /// Closes the Throughput Run opened by the preceding <see cref="RunStarted"/>. Every
    /// producer publishes it from a <c>finally</c>, including when the run failed or was
    /// cancelled: an unclosed run would strand the next run's total.
    /// </summary>
    public sealed record RunEnded : LlmStreamEvent;

    /// <param name="ParagraphPreview">Short label for the request, shown in the stream panel.</param>
    /// <param name="Prompt">The full rendered prompt.</param>
    /// <param name="ConfigId">
    /// Id of the config serving this request. Throughput is keyed by id and the display name is
    /// resolved at render, so a mid-run rename doesn't split a config's row in two. Config is
    /// known at request start, which is why it rides this event and not <see cref="StreamCompleted"/>.
    /// </param>
    /// <param name="ConfigName">The config's display name at request time.</param>
    public sealed record RequestStarted(
        string ParagraphPreview, string Prompt, int ConfigId, string ConfigName) : LlmStreamEvent;
    public sealed record ThinkingDelta(string Text) : LlmStreamEvent;
    public sealed record ContentDelta(string Text) : LlmStreamEvent;
    /// <summary>
    /// A request's final server-measured figures. Every field is nullable and absence is
    /// first-class: a backend that sends no <c>timings</c>, or a fully cache-hit prompt whose
    /// numbers llama.cpp serializes as null, yields nulls here. A surface renders those as
    /// nothing — never as <c>0</c>, which would mean "it generated nothing" (ADR 0003).
    /// </summary>
    /// <param name="TokensIn">Prompt tokens, from <c>usage.prompt_tokens</c>.</param>
    /// <param name="TokensOut">Generated tokens, from <c>timings.predicted_n</c>.</param>
    /// <param name="GenerationMs">
    /// Server-measured generation time (<c>timings.predicted_ms</c>), spanning first token →
    /// stream end with model load and queue wait excluded. This field replaced a wall-clock
    /// <c>ElapsedSeconds</c>; the rename is deliberate, because the meaning changed.
    /// </param>
    /// <param name="TokensPerSecond">
    /// Request Throughput, recomputed from <c>predicted_n ÷ predicted_ms</c> rather than read
    /// from the server's ready-made <c>predicted_per_second</c>, so that a request rate and a run
    /// total stay arithmetically consistent.
    /// </param>
    public sealed record StreamCompleted(int? TokensIn, int? TokensOut,
        double? GenerationMs, double? TokensPerSecond) : LlmStreamEvent;
    public sealed record StreamFailed(string Reason) : LlmStreamEvent;

    /// <summary>
    /// A request that died mid-stream, carrying the last reading it received. Published on both
    /// the cancel and the failure path, because an aborted request's measurement counts: it is
    /// real work, really measured, and excluding it makes a run total less accurate, not more
    /// (ADR 0003). An aborted stream sends no final chunk at all, so these figures come from the
    /// last <c>timings_per_token</c> reading that arrived before the abort.
    /// </summary>
    /// <remarks>
    /// <b>This event reports measurement, not outcome.</b> That separation is the whole reason it
    /// exists rather than the two cheaper alternatives:
    /// <list type="bullet">
    /// <item>Publishing <see cref="StreamCompleted"/> before rethrowing would have needed no
    /// aggregator change at all, but the stream did <i>not</i> complete — a subscriber reading that
    /// event as "the request succeeded" would be wrong. A same-named event with a changed meaning
    /// is the trap the <c>ElapsedSeconds</c>→<c>GenerationMs</c> rename already avoided once.</item>
    /// <item>Widening <see cref="StreamFailed"/> with these figures cannot cover cancellation: a
    /// cancel is not a service failure and must surface no error, yet <c>LlmStreamView</c> renders
    /// a <see cref="StreamFailed"/>'s reason in the error colour.</item>
    /// </list>
    /// So the failure path publishes <b>both</b> — this for the figures, then
    /// <see cref="StreamFailed"/> for the error — while the cancel path publishes only this one,
    /// and stays silent about outcome exactly as it always has.
    /// <para>
    /// Every field is nullable, as on <see cref="StreamCompleted"/>. A request that died before its
    /// first reading reports absence rather than <c>0</c>, and must not zero the total it joins.
    /// There is no <c>TokensIn</c>: <c>usage</c> rides the final chunk, which an aborted stream
    /// never sends.
    /// </para>
    /// </remarks>
    /// <param name="TokensOut">Generated tokens, from the last <c>timings.predicted_n</c> received.</param>
    /// <param name="GenerationMs">Server-measured generation time, from the last <c>timings.predicted_ms</c> received.</param>
    /// <param name="TokensPerSecond">
    /// The request's rate at the moment it died, recomputed from <c>predicted_n ÷ predicted_ms</c>.
    /// </param>
    public sealed record StreamAborted(
        int? TokensOut, double? GenerationMs, double? TokensPerSecond) : LlmStreamEvent;

    /// <summary>
    /// Announces that the attribution chain is about to run a step ≥ 1 (an escalation).
    /// Published before each escalation step with the 1-based step index, the config being
    /// tried, and the count of suspect items entering that step. Step 0 (the primary) is not
    /// announced. The stream-panel subscriber renders or ignores it gracefully.
    /// </summary>
    public sealed record EscalationStarted(int Step, string ConfigName, int ItemCount) : LlmStreamEvent;
}
