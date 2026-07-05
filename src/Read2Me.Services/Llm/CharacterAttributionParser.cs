using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Tolerant parser for the LLM character-attribution JSON response.
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first {...} block from the raw text.
    /// </summary>
    public static class CharacterAttributionParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out CharacterAttributionResult result)
        {
            result = default!;
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

            // Extract from first { to last }
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return false;

            var json = text[start..(end + 1)];

            try
            {
                var dto = JsonSerializer.Deserialize<AttributionDto>(json, JsonOptions);
                if (dto == null || string.IsNullOrWhiteSpace(dto.Character))
                    return false;

                result = new CharacterAttributionResult(dto.Character, dto.VoiceInstructions ?? string.Empty, dto.Reasoning);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed class AttributionDto
        {
            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("character")]
            public string? Character { get; set; }

            [JsonPropertyName("voice_instructions")]
            public string? VoiceInstructions { get; set; }
        }
    }
}
