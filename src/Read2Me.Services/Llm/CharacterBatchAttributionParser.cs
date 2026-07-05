using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Tolerant parser for the LLM batch character-attribution JSON response.
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first [...] block from the raw text. Duplicate indexes: first entry wins.
    /// Entries without a usable index or character are dropped; returns false only
    /// when no array parses at all.
    /// </summary>
    public static class CharacterBatchAttributionParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out IReadOnlyDictionary<int, CharacterAttributionResult> results)
        {
            results = default!;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var text = raw.Trim();

            // Strip ``` fences (```json ... ``` or ``` ... ```)
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                    text = text[(firstNewline + 1)..];
                if (text.EndsWith("```", StringComparison.Ordinal))
                    text = text[..^3].TrimEnd();
            }

            // Extract from first [ to last ]
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start < 0 || end <= start)
                return false;

            var json = text[start..(end + 1)];

            try
            {
                var dtos = JsonSerializer.Deserialize<List<BatchEntryDto>>(json, JsonOptions);
                if (dtos == null)
                    return false;

                var map = new Dictionary<int, CharacterAttributionResult>();
                foreach (var dto in dtos)
                {
                    if (dto.Index is not { } index || index < 0 || string.IsNullOrWhiteSpace(dto.Character))
                        continue;
                    if (!map.ContainsKey(index))
                        map[index] = new CharacterAttributionResult(dto.Character, dto.VoiceInstructions ?? string.Empty, dto.Reasoning);
                }

                results = map;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed class BatchEntryDto
        {
            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("character")]
            public string? Character { get; set; }

            [JsonPropertyName("voice_instructions")]
            public string? VoiceInstructions { get; set; }
        }
    }
}
