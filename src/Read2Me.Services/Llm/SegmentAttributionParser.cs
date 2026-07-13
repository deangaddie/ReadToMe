using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Tolerant parser for the LLM segment-attribution JSON response (single and batch).
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first {...} or [...] block from the raw text.
    /// </summary>
    public static class SegmentAttributionParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out SegmentAttributionResult result)
        {
            result = default!;
            if (!TryExtractJson(raw, '{', '}', out var json))
                return false;

            try
            {
                var dto = JsonSerializer.Deserialize<AnswerDto>(json, JsonOptions);
                if (dto == null || !TryMapSegments(dto.Segments, out var segments))
                    return false;

                result = new SegmentAttributionResult(dto.Reasoning ?? string.Empty, segments);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Parses a batch response. Every index in <paramref name="requestedIndexes"/> must be
        /// answered with usable segments or the whole parse fails; extra (unrequested) indexes are
        /// ignored — both trial models also answer for context paragraphs. Duplicate indexes:
        /// first entry wins. Note the contrast with <see cref="CharacterBatchAttributionParser"/>,
        /// which tolerantly drops unusable entries: here escalation needs the whole paragraph
        /// (any unanswered index = ParseFailure), so the parse is all-or-nothing.
        /// </summary>
        public static bool TryParseBatch(
            string raw, IReadOnlyCollection<int> requestedIndexes,
            out IReadOnlyDictionary<int, SegmentAttributionResult> results)
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
                var map = new Dictionary<int, SegmentAttributionResult>();
                foreach (var dto in dtos)
                {
                    if (dto.Index is not { } index || !requested.Contains(index) || map.ContainsKey(index))
                        continue;
                    if (!TryMapSegments(dto.Segments, out var segments))
                        return false;
                    map[index] = new SegmentAttributionResult(dto.Reasoning ?? string.Empty, segments);
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

        /// <summary>Maps wire segments to <see cref="AttributionSegment"/>s; false when unusable.</summary>
        private static bool TryMapSegments(List<SegmentDto>? dtos, out IReadOnlyList<AttributionSegment> segments)
        {
            segments = default!;
            if (dtos == null || dtos.Count == 0)
                return false;

            var mapped = new List<AttributionSegment>(dtos.Count);
            foreach (var dto in dtos)
            {
                if (dto.Text == null)
                    return false;
                var type = dto.Type?.Trim().ToLowerInvariant() switch
                {
                    SegmentWire.Narration => AttributionSegmentType.Narration,
                    SegmentWire.Dialog => AttributionSegmentType.Dialog,
                    _ => (AttributionSegmentType?)null,
                };
                if (type == null)
                    return false;

                // Narration always speaks as the narrator with no instructions, whatever the model
                // answered; a dialog segment without a speaker violates the schema → parse failure
                // (ParseFailure tier, not a silent "unknown" repair).
                var speaker = dto.Speaker?.Trim();
                if (type == AttributionSegmentType.Dialog && string.IsNullOrEmpty(speaker))
                    return false;
                var isNarration = type == AttributionSegmentType.Narration;
                mapped.Add(new AttributionSegment(
                    UnescapeLiteralUnicode(dto.Text),
                    type.Value,
                    isNarration ? SegmentWire.Narrator : speaker!,
                    isNarration ? string.Empty : dto.VoiceInstructions ?? string.Empty));
            }

            segments = mapped;
            return true;
        }

        /// <summary>
        /// Models sometimes double-escape, leaving literal \uXXXX sequences in the parsed string;
        /// fold them back to their characters so text comparison sees the real book text.
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

            [JsonPropertyName("segments")]
            public List<SegmentDto>? Segments { get; set; }
        }

        private sealed class BatchEntryDto
        {
            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("segments")]
            public List<SegmentDto>? Segments { get; set; }
        }

        private sealed class SegmentDto
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("speaker")]
            public string? Speaker { get; set; }

            [JsonPropertyName("voice_instructions")]
            public string? VoiceInstructions { get; set; }
        }
    }
}
