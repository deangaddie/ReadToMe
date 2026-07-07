using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.BookEdits
{
    /// <summary>
    /// Tolerant parser for the LLM batch edit JSON response.
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first [...] block. When the array is truncated (generation hit the token or
    /// context limit mid-entry), the complete entries before the cut are salvaged so
    /// only the missing items fail. Duplicate indexes: first entry wins. Entries without
    /// a usable index or new_text are dropped; returns false only when no entry parses.
    /// </summary>
    public static class BookEditBatchParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public static bool TryParse(string raw, out IReadOnlyDictionary<int, string> results)
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
            if (start < 0)
                return false;

            var end = text.LastIndexOf(']');
            if (end > start && TryParseWholeArray(text[start..(end + 1)], out results))
                return true;

            // Truncated or malformed array: salvage the complete entries.
            var salvaged = SalvageEntries(text[start..]);
            if (salvaged.Count == 0)
                return false;

            results = salvaged;
            return true;
        }

        private static bool TryParseWholeArray(string json, out IReadOnlyDictionary<int, string> results)
        {
            results = default!;
            try
            {
                var dtos = JsonSerializer.Deserialize<List<BatchEntryDto>>(json, JsonOptions);
                if (dtos == null)
                    return false;

                var map = new Dictionary<int, string>();
                foreach (var dto in dtos)
                    Add(map, dto);

                results = map;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Walks the array body string-aware, deserializing each complete top-level
        /// {...} object individually and skipping the incomplete tail.
        /// </summary>
        private static Dictionary<int, string> SalvageEntries(string arrayText)
        {
            var map = new Dictionary<int, string>();
            var depth = 0;
            var objStart = -1;
            var inString = false;
            var escaped = false;

            for (var i = 1; i < arrayText.Length; i++) // skip the opening '['
            {
                var c = arrayText[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        if (depth == 0)
                            objStart = i;
                        depth++;
                        break;
                    case '}':
                        if (depth > 0 && --depth == 0)
                        {
                            try
                            {
                                var dto = JsonSerializer.Deserialize<BatchEntryDto>(
                                    arrayText[objStart..(i + 1)], JsonOptions);
                                if (dto != null)
                                    Add(map, dto);
                            }
                            catch (JsonException)
                            {
                                // skip malformed entry, keep scanning
                            }
                        }
                        break;
                    case ']':
                        if (depth == 0)
                            return map; // array closed
                        break;
                }
            }
            return map;
        }

        private static void Add(Dictionary<int, string> map, BatchEntryDto dto)
        {
            if (dto.Index is not { } index || index < 0 || dto.NewText == null)
                return;
            if (!map.ContainsKey(index))
                map[index] = dto.NewText;
        }

        private sealed class BatchEntryDto
        {
            [JsonPropertyName("index")]
            public int? Index { get; set; }

            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("new_text")]
            public string? NewText { get; set; }
        }
    }
}
