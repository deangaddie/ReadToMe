using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    /// <summary>Parses raw LLM output into <typeparamref name="T"/>; false with an error on failure.</summary>
    public delegate bool TryParse<T>(string raw, out T? value, out string? error);

    /// <summary>Expected top-level completion kind — drives the early-stop JSON completion scan.</summary>
    public enum CompletionShape
    {
        /// <summary>Stop once the first top-level JSON object closes.</summary>
        Object,
        /// <summary>Stop once the first top-level JSON array closes.</summary>
        Array,
        /// <summary>Free text — read the stream to its end, no scanner.</summary>
        None,
    }

    public enum LlmRunOutcome
    {
        Completed,
        ParseFailed,
        Failed,
        ServiceUnavailable,
        /// <summary>
        /// A switchable llama endpoint stayed responsive but its target model had not finished
        /// loading within the budget. Provider is busy, not dead: the health monitor is bypassed and
        /// the caller waits/retries rather than escalating to the next config (which would evict the
        /// load in progress).
        /// </summary>
        ModelLoading,
    }

    /// <summary>
    /// Per-run parameters that are not the server's persisted settings. A set property wins over the
    /// config's own value for this request only; a null one falls back to the config. Kept to the
    /// two parameters callers actually vary per run — do not grow it speculatively.
    /// </summary>
    public sealed record LlmRunOverrides(int? MaxTokens = null, double? Temperature = null);

    /// <param name="Config">Server to call.</param>
    /// <param name="Prompt">Full rendered prompt.</param>
    /// <param name="Label">Short label shown in the live-stream panel's RequestStarted event.</param>
    /// <param name="JsonSchema">Optional schema for grammar-constrained completion.</param>
    /// <param name="Shape">Completion kind for the early-stop scan.</param>
    /// <param name="DisableThinking">
    /// Ask the server to skip the hidden thinking phase. Thinking dominates generation time on
    /// thinking models (measured 75–95% of output tokens on attribution batches) and helps only
    /// recall-heavy asks: keep it for character discovery and voice plans, disable it for
    /// attribution and voice-design prompts where measured accuracy holds without it.
    /// </param>
    /// <param name="Overrides">
    /// Per-run parameters that are not the server's persisted settings; null leaves the config's
    /// own values in force.
    /// </param>
    public sealed record LlmRunRequest(
        LlmServerConfig Config, string Prompt, string Label,
        string? JsonSchema = null, CompletionShape Shape = CompletionShape.Object,
        bool DisableThinking = false, LlmRunOverrides? Overrides = null);

    /// <param name="Outcome">How the run ended.</param>
    /// <param name="Value">Parsed value on <see cref="LlmRunOutcome.Completed"/>; default otherwise.</param>
    /// <param name="Raw">Full accumulated content, whatever the outcome.</param>
    /// <param name="Error">Failure reason for non-Completed outcomes.</param>
    public sealed record LlmRunResult<T>(LlmRunOutcome Outcome, T? Value, string Raw, string? Error);

    /// <summary>
    /// The one place a streamed LLM completion is run: owns the live-stream event lifecycle,
    /// token metrics, early-stop completion scan, health-streak reporting, cancel-vs-timeout
    /// semantics and parse-failure mapping. Feature code builds a prompt, calls RunAsync and
    /// switches on the four outcomes — it never touches <see cref="ILlmClient"/> directly.
    /// Genuine cancellation (the caller's token) throws <see cref="OperationCanceledException"/>
    /// through; every other failure comes back as an outcome.
    /// </summary>
    public interface ILlmCompletionRunner
    {
        /// <summary>Runs a completion and parses it; parse failure maps to <see cref="LlmRunOutcome.ParseFailed"/>.</summary>
        Task<LlmRunResult<T>> RunAsync<T>(LlmRunRequest request, TryParse<T> parser, CancellationToken ct);

        /// <summary>Runs a free-text completion; on success <c>Value == Raw</c>.</summary>
        Task<LlmRunResult<string>> RunAsync(LlmRunRequest request, CancellationToken ct);
    }
}
