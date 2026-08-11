using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Tolerant parser for the LLM item-attribution JSON response (single and batch).
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first {...} or [...] block from the raw text.
    /// </summary>
    public static class ItemAttributionParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out ItemAttributionResult result)
        {
            result = default!;
            if (!TryExtractJson(raw, '{', '}', out var json))
                return false;

            try
            {
                var dto = JsonSerializer.Deserialize<AnswerDto>(json, JsonOptions);
                if (dto == null)
                    return false;

                result = new ItemAttributionResult(dto.Reasoning ?? string.Empty, MapItems(dto.Items));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Parses a batch response. Every paragraph index in <paramref name="requestedIndexes"/> must
        /// be answered or the whole parse fails; extra (unrequested) indexes are ignored — both trial
        /// models also answer for context paragraphs. Duplicate indexes: first entry wins. Note the
        /// contrast with <see cref="CharacterBatchAttributionParser"/>, which tolerantly drops
        /// unusable entries: here escalation needs the whole paragraph (any unanswered paragraph
        /// index = ParseFailure), so the parse is all-or-nothing at chunk level. Tolerance applies
        /// only *within* an answered paragraph, where unusable items are dropped.
        /// </summary>
        public static bool TryParseBatch(
            string raw, IReadOnlyCollection<int> requestedIndexes,
            out IReadOnlyDictionary<int, ItemAttributionResult> results)
        {
            results = default!;
            if (!TryExtractJson(raw, '[', ']', out var json))
                return false;

            try
            {
                var dtos = JsonSerializer.Deserialize<List<BatchEntryDto>>(json, JsonOptions);
                if (dtos == null)
                    return false;

                var requested = new HashSet<int>(requestedIndexes);
                var map = new Dictionary<int, ItemAttributionResult>();
                foreach (var dto in dtos)
                {
                    if (dto.Index is not { } index || !requested.Contains(index) || map.ContainsKey(index))
                        continue;
                    map[index] = new ItemAttributionResult(dto.Reasoning ?? string.Empty, MapItems(dto.Items));
                }

                if (map.Count != requested.Count)
                    return false;

                results = map;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Strips ``` fences and surrounding prose, extracting from the first
        /// <paramref name="open"/> to the last <paramref name="close"/>.
        /// </summary>
        private static bool TryExtractJson(string raw, char open, char close, out string json)
        {
            json = default!;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var text = raw.Trim();

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                    text = text[(firstNewline + 1)..];
                if (text.EndsWith("```", StringComparison.Ordinal))
                    text = text[..^3].TrimEnd();
            }

            var start = text.IndexOf(open);
            var end = text.LastIndexOf(close);
            if (start < 0 || end <= start)
                return false;

            json = text[start..(end + 1)];
            return true;
        }

        /// <summary>
        /// Maps wire items to <see cref="AttributedItem"/>s. Item-level tolerance: an entry with no
        /// usable index or no speaker is dropped rather than failing the paragraph, and a duplicate
        /// index keeps the first answer. An absent or empty list maps to zero attributions — a valid
        /// answer whose unattributed dialog items escalate as unknown.
        /// </summary>
        private static IReadOnlyList<AttributedItem> MapItems(List<ItemDto>? dtos)
        {
            if (dtos == null || dtos.Count == 0)
                return [];

            var mapped = new List<AttributedItem>(dtos.Count);
            var seen = new HashSet<int>();
            foreach (var dto in dtos)
            {
                var speaker = dto.Speaker?.Trim();
                if (string.IsNullOrEmpty(speaker))
                    continue;
                if (!TryReadIndex(dto.Index, out var index) || !seen.Add(index))
                    continue;

                // Instructions pass through as answered, null included: an item the model named but
                // gave no instructions for is cleared, not left holding a previous run's direction.
                mapped.Add(new AttributedItem(
                    index,
                    UnescapeLiteralUnicode(speaker),
                    dto.VoiceInstructions));
            }

            return mapped;
        }

        /// <summary>
        /// Reads the item index tolerantly: models answer it as a number, and occasionally as a
        /// string. Anything else (absent, null, fractional, prose) drops the item.
        /// </summary>
        private static bool TryReadIndex(JsonElement raw, out int index)
        {
            index = default;
            return raw.ValueKind switch
            {
                JsonValueKind.Number => raw.TryGetInt32(out index),
                JsonValueKind.String => int.TryParse(raw.GetString(), out index),
                _ => false,
            };
        }

        /// <summary>
        /// Models sometimes double-escape, leaving literal \uXXXX sequences in the parsed string;
        /// fold them back to their characters so the name matches the roster.
        /// </summary>
        private static string UnescapeLiteralUnicode(string text) =>
            text.Contains("\\u", StringComparison.OrdinalIgnoreCase)
                ? Regex.Replace(text, @"\\u([0-9a-fA-F]{4})",
                    m => ((char)Convert.ToUInt16(m.Groups[1].Value, 16)).ToString())
                : text;

        private sealed class AnswerDto
        {
            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("items")]
            public List<ItemDto>? Items { get; set; }
        }

        private sealed class BatchEntryDto
        {
            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("items")]
            public List<ItemDto>? Items { get; set; }
        }

        private sealed class ItemDto
        {
            /// <summary>Raw so a malformed index drops one item instead of failing the paragraph.</summary>
            [JsonPropertyName("index")]
            public JsonElement Index { get; set; }

            [JsonPropertyName("speaker")]
            public string? Speaker { get; set; }

            [JsonPropertyName("voice_instructions")]
            public string? VoiceInstructions { get; set; }
        }
    }
}
