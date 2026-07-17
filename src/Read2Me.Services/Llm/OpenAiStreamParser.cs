using System.Text.Json;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Pure translation of OpenAI-style SSE stream lines into <see cref="LlmChatChunk"/>.
    /// No I/O. Single source of truth for the streaming wire format.
    /// </summary>
    public static class OpenAiStreamParser
    {
        public enum LineKind { Ignore, Chunk, Done }

        public readonly record struct LineResult(LineKind Kind, LlmChatChunk? Chunk);

        public static readonly LineResult Ignored = new(LineKind.Ignore, null);
        public static readonly LineResult DoneResult = new(LineKind.Done, null);

        /// <summary>
        /// Interprets a single SSE line. Blank/non-data/unparseable -> Ignore.
        /// "data: [DONE]" -> Done. Otherwise content/thinking chunk.
        /// </summary>
        public static LineResult ParseLine(string line)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                return Ignored;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
                return DoneResult;

            var chunk = ParseChunk(payload);
            return chunk is null ? Ignored : new LineResult(LineKind.Chunk, chunk);
        }

        /// <summary>
        /// Parses a JSON chunk payload. Returns null only when the payload is malformed or carries
        /// nothing at all — no text and no metrics.
        /// </summary>
        /// <remarks>
        /// <c>timings</c> and <c>usage</c> are root-level siblings of <c>choices</c>, and the chunk
        /// carrying them is <b>delta-less</b> (<c>delta: {}</c>, or <c>choices: []</c> once
        /// <c>include_usage</c> is on). So the root is read before and independently of the delta:
        /// a delta-first parser drops every metrics chunk llama.cpp sends.
        /// </remarks>
        public static LlmChatChunk? ParseChunk(string payload)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { return null; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                var timings = ReadTimings(root);
                var usage = ReadUsage(root);

                string? content = null;
                string? thinking = null;

                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta))
                {
                    content = ReadString(delta, "content");
                    thinking = ReadString(delta, "reasoning_content")
                               ?? ReadString(delta, "reasoning");
                }

                if (content is null && thinking is null && timings is null && usage is null)
                    return null;

                return new LlmChatChunk(thinking, content, Done: false, timings, usage);
            }
        }

        public static string Combine(string baseUrl, string path) =>
            $"{baseUrl.TrimEnd('/')}/{path}";

        private static LlmTimings? ReadTimings(JsonElement root)
        {
            if (!root.TryGetProperty("timings", out var timings) ||
                timings.ValueKind != JsonValueKind.Object)
                return null;

            return new LlmTimings(
                ReadInt(timings, "cache_n"),
                ReadInt(timings, "prompt_n"),
                ReadDouble(timings, "prompt_ms"),
                ReadInt(timings, "predicted_n"),
                ReadDouble(timings, "predicted_ms"));
        }

        private static LlmUsage? ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
                return null;

            int? cached = usage.TryGetProperty("prompt_tokens_details", out var details) &&
                          details.ValueKind == JsonValueKind.Object
                ? ReadInt(details, "cached_tokens")
                : null;

            return new LlmUsage(
                ReadInt(usage, "prompt_tokens"),
                ReadInt(usage, "completion_tokens"),
                ReadInt(usage, "total_tokens"),
                cached);
        }

        // A field llama.cpp could not compute serializes as JSON null (its divides have no zero
        // guard). It stays null here; it never becomes 0.
        private static int? ReadInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number)
                ? number
                : null;

        private static double? ReadDouble(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number)
                ? number
                : null;

        private static string? ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
