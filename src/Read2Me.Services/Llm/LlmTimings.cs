namespace Read2Me.Services.Llm
{
    /// <summary>
    /// llama.cpp's own measurement of a request, read from the root-level <c>timings</c>
    /// object on a stream chunk. Server-measured: model load and queue wait are in neither
    /// counter.
    /// </summary>
    /// <remarks>
    /// Every field is nullable and absence is first-class — llama.cpp divides without a zero
    /// guard, so a fully cache-hit prompt serializes numbers as JSON <c>null</c>. Never coerce
    /// a null to <c>0</c>: "we don't know" and "it did nothing" are different answers (ADR 0003).
    /// <para>
    /// Values are <b>cumulative running totals</b>, not per-chunk instantaneous readings. With
    /// <c>timings_per_token: true</c> every chunk restates the totals so far; a consumer that
    /// sums them double-counts every token. Replace, never accrue.
    /// </para>
    /// <para>
    /// <see cref="PredictedMs"/> spans exactly first-token → stream-end. <see cref="PromptMs"/>
    /// is re-assigned at the first token, so it *includes* that token's decode — it is
    /// time-to-first-token from prompt start, not the bare prompt pass. The two do not sum to
    /// request latency: queue wait before prompt processing is unmeasured.
    /// </para>
    /// <para>
    /// The server also sends ready-made rates (<c>predicted_per_second</c> and the per-token
    /// millisecond fields). They are deliberately not modelled: all figures recompute from
    /// <see cref="PredictedN"/> ÷ <see cref="PredictedMs"/> so a request rate and a run total
    /// are always arithmetically consistent.
    /// </para>
    /// </remarks>
    /// <param name="CacheN">Prompt tokens reused from cache rather than re-evaluated.</param>
    /// <param name="PromptN">Prompt tokens actually processed (excludes <paramref name="CacheN"/>).</param>
    /// <param name="PromptMs">Prompt-processing start → first token, in milliseconds.</param>
    /// <param name="PredictedN">Tokens generated so far.</param>
    /// <param name="PredictedMs">First token → now, in milliseconds.</param>
    public sealed record LlmTimings(
        int? CacheN,
        int? PromptN,
        double? PromptMs,
        int? PredictedN,
        double? PredictedMs);

    /// <summary>
    /// OpenAI-style token counts, read from the root-level <c>usage</c> object on a stream chunk.
    /// Only sent when the request asks for it via <c>stream_options.include_usage</c> — a
    /// different gate from <see cref="LlmTimings"/>, which rides the final chunk ungated.
    /// </summary>
    /// <remarks>Fields are individually nullable; absence is first-class.</remarks>
    public sealed record LlmUsage(
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        int? CachedTokens);
}
