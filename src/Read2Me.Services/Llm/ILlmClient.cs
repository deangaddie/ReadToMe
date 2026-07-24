using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    public interface ILlmClient
    {
        /// <summary>
        /// Streams a chat completion for <paramref name="prompt"/> using the given config.
        /// Yields incremental thinking/content deltas as they arrive.
        /// When <paramref name="jsonSchema"/> is set, the request asks the server to constrain
        /// output to that JSON schema (OpenAI response_format json_schema; llama.cpp compiles
        /// it to a grammar so the model cannot emit anything but the schema).
        /// When <paramref name="disableThinking"/> is set, the request asks the server's chat
        /// template to skip the hidden thinking phase (llama.cpp chat_template_kwargs
        /// enable_thinking=false); no-op on models without a thinking mode.
        /// When <paramref name="overrides"/> is set, its non-null properties replace the config's
        /// own values for this request only; the config itself is untouched.
        /// </summary>
        IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
            LlmServerConfig config, string prompt, string? jsonSchema = null,
            bool disableThinking = false, LlmRunOverrides? overrides = null,
            CancellationToken ct = default);

        /// <summary>
        /// Fetches the list of available model ids from the server's models endpoint.
        /// Throws on failure so the caller can fall back to free-text model entry.
        /// </summary>
        Task<IReadOnlyList<string>> GetModelsAsync(
            LlmServerConfig config, CancellationToken ct = default);
    }
}
