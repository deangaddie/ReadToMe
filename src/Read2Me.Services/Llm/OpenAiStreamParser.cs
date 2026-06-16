using System;
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

        /// <summary>Parses a JSON chunk payload. Returns null for malformed or empty deltas.</summary>
        public static LlmChatChunk? ParseChunk(string payload)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch (JsonException) { return null; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                    return null;

                var first = choices[0];
                if (!first.TryGetProperty("delta", out var delta))
                    return null;

                string? content = ReadString(delta, "content");
                string? thinking = ReadString(delta, "reasoning_content")
                                   ?? ReadString(delta, "reasoning");

                if (content is null && thinking is null)
                    return null;

                return new LlmChatChunk(thinking, content, Done: false);
            }
        }

        public static string Combine(string baseUrl, string path) =>
            $"{baseUrl.TrimEnd('/')}/{path}";

        private static string? ReadString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
