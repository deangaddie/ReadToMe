using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Tolerant parser for the LLM character-discovery JSON response. Handles code fences
    /// (```json ... ```) and leading/trailing prose by extracting the first {...} block.
    /// An entry with no usable name is dropped; a missing "aliases" becomes an empty list
    /// and null/blank aliases are discarded. Returns false only when the response is not a
    /// parseable object (there is no "characters" array at all) — a well-formed object with
    /// an empty array parses to an empty list.
    /// </summary>
    public static class CharacterDiscoveryParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out IReadOnlyList<DiscoveredCharacter> characters, out string? error)
        {
            characters = [];
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Empty response.";
                return false;
            }

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
            {
                error = "No JSON object found in response.";
                return false;
            }

            DiscoveryDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<DiscoveryDto>(text[start..(end + 1)], JsonOptions);
            }
            catch (JsonException)
            {
                error = "Response was not valid JSON.";
                return false;
            }

            if (dto?.Characters == null)
            {
                error = "Response has no \"characters\" array.";
                return false;
            }

            var result = new List<DiscoveredCharacter>();
            foreach (var c in dto.Characters)
            {
                if (string.IsNullOrWhiteSpace(c?.Name))
                    continue;
                var aliases = (c.Aliases ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a!.Trim())
                    .ToList();
                result.Add(new DiscoveredCharacter(c.Name.Trim(), aliases));
            }

            characters = result;
            return true;
        }

        private sealed class DiscoveryDto
        {
            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("characters")]
            public List<CharacterDto>? Characters { get; set; }
        }

        private sealed class CharacterDto
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("aliases")]
            public List<string?>? Aliases { get; set; }
        }
    }
}
