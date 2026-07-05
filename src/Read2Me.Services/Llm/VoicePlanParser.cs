using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Tolerant parser for the LLM voice-plan JSON response.
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first [...] block from the raw text.
    /// </summary>
    public static class VoicePlanParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out IReadOnlyList<VoicePlanVoice> voices)
        {
            voices = [];
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
                var dtos = JsonSerializer.Deserialize<List<VoiceDto>>(json, JsonOptions);
                if (dtos == null)
                    return false;

                var result = new List<VoicePlanVoice>();
                foreach (var dto in dtos)
                {
                    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.DesignPrompt))
                        continue;
                    result.Add(new VoicePlanVoice(
                        dto.Name.Trim(),
                        string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
                        dto.DesignPrompt.Trim()));
                }

                if (result.Count == 0)
                    return false;

                voices = result;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private sealed class VoiceDto
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("design_prompt")]
            public string? DesignPrompt { get; set; }
        }
    }
}
