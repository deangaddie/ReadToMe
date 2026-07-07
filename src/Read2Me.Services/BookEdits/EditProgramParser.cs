using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Read2Me.Services.BookEdits
{
    /// <summary>
    /// Tolerant parser for the LLM edit-program JSON response.
    /// Handles code fences (```json ... ```) and leading/trailing prose by extracting
    /// the first {...} block. Validates transform fields and any LLM-emitted regexes;
    /// a validation failure yields false with a message suitable for the UI.
    /// </summary>
    public static class EditProgramParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        public static bool TryParse(string raw, out EditProgram? program, out string? error)
        {
            program = null;
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

            var json = text[start..(end + 1)];

            ProgramDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ProgramDto>(json, JsonOptions);
            }
            catch (JsonException)
            {
                error = "Response was not valid JSON.";
                return false;
            }

            if (dto == null)
            {
                error = "Response was not valid JSON.";
                return false;
            }

            if (dto.Supported == false)
            {
                program = new EditProgram(
                    Supported: false,
                    UnsupportedReason: dto.UnsupportedReason,
                    Target: EditTargetSelector.ChapterTitle,
                    NodeFilter: NodeFilter.All,
                    ParagraphFilter: ParagraphFilter.All,
                    Transform: new EditTransform(TransformKind.Llm),
                    Reasoning: dto.Reasoning);
                return true;
            }

            if (!TryMapTarget(dto.Target, out var target))
            {
                error = $"Unknown target '{dto.Target}'.";
                return false;
            }

            if (dto.Transform == null || !TryMapTransformKind(dto.Transform.Kind, out var kind))
            {
                error = $"Unknown transform kind '{dto.Transform?.Kind}'.";
                return false;
            }

            switch (kind)
            {
                case TransformKind.RegexReplace when string.IsNullOrEmpty(dto.Transform.Pattern):
                    error = "Transform 'regex_replace' is missing its pattern.";
                    return false;
                case TransformKind.SetTemplate when string.IsNullOrEmpty(dto.Transform.Template):
                    error = "Transform 'set_template' is missing its template.";
                    return false;
                case TransformKind.Llm when string.IsNullOrWhiteSpace(dto.Transform.Instruction):
                    error = "Transform 'llm' is missing its instruction.";
                    return false;
            }

            if (!ValidateRegex(dto.Transform.Pattern, "transform pattern", ref error) ||
                !ValidateRegex(dto.NodeFilter?.TitleRegex, "title filter", ref error))
                return false;

            if (!TryMapPredicates(dto.ParagraphFilter?.Where, out var where, ref error))
                return false;

            program = new EditProgram(
                Supported: true,
                UnsupportedReason: null,
                Target: target,
                NodeFilter: new NodeFilter(dto.NodeFilter?.OrdinalFrom, dto.NodeFilter?.OrdinalTo, dto.NodeFilter?.TitleRegex),
                ParagraphFilter: new ParagraphFilter(where),
                Transform: new EditTransform(kind, dto.Transform.Pattern, dto.Transform.Replacement, dto.Transform.Template, dto.Transform.Instruction),
                Reasoning: dto.Reasoning);
            return true;
        }

        private static bool ValidateRegex(string? pattern, string label, ref string? error)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            try
            {
                _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
                return true;
            }
            catch (ArgumentException)
            {
                error = $"Invalid regular expression in {label}: {pattern}";
                return false;
            }
        }

        private static bool TryMapTarget(string? value, out EditTargetSelector target)
        {
            target = value switch
            {
                "volume_title" => EditTargetSelector.VolumeTitle,
                "part_title" => EditTargetSelector.PartTitle,
                "chapter_title" => EditTargetSelector.ChapterTitle,
                "paragraph_text" => EditTargetSelector.ParagraphText,
                _ => (EditTargetSelector)(-1),
            };
            return (int)target >= 0;
        }

        private static bool TryMapTransformKind(string? value, out TransformKind kind)
        {
            kind = value switch
            {
                "regex_replace" => TransformKind.RegexReplace,
                "set_template" => TransformKind.SetTemplate,
                "llm" => TransformKind.Llm,
                _ => (TransformKind)(-1),
            };
            return (int)kind >= 0;
        }

        private static bool TryMapPredicates(
            List<PredicateDto>? dtos, out IReadOnlyList<EditPredicate> where, ref string? error)
        {
            var result = new List<EditPredicate>();
            where = result;
            if (dtos == null)
                return true;

            foreach (var dto in dtos)
            {
                if (!TryMapField(dto.Field, out var field))
                {
                    error = $"Unknown filter field '{dto.Field}'.";
                    return false;
                }
                if (!TryMapOp(dto.Op, out var op))
                {
                    error = $"Unknown filter op '{dto.Op}'.";
                    return false;
                }

                if (field == PredicateField.Text)
                {
                    if (op != PredicateOp.Regex || string.IsNullOrEmpty(dto.Regex))
                    {
                        error = "Filter on 'text' must use op 'regex' with a regex.";
                        return false;
                    }
                    if (!ValidateRegex(dto.Regex, "paragraph filter", ref error))
                        return false;
                }
                else
                {
                    if (op == PredicateOp.Regex)
                    {
                        error = $"Filter op 'regex' is only valid on the 'text' field, not '{dto.Field}'.";
                        return false;
                    }
                    if (dto.Value == null)
                    {
                        error = $"Filter on '{dto.Field}' is missing its value.";
                        return false;
                    }
                    if (op == PredicateOp.Between && dto.ValueTo == null)
                    {
                        error = $"Filter op 'between' on '{dto.Field}' is missing value_to.";
                        return false;
                    }
                }

                result.Add(new EditPredicate(field, op, dto.Value, dto.ValueTo, dto.Regex));
            }
            return true;
        }

        private static bool TryMapField(string? value, out PredicateField field)
        {
            field = value switch
            {
                "paragraph_ordinal" => PredicateField.ParagraphOrdinal,
                "paragraph_ordinal_from_end" => PredicateField.ParagraphOrdinalFromEnd,
                "item_ordinal" => PredicateField.ItemOrdinal,
                "text" => PredicateField.Text,
                _ => (PredicateField)(-1),
            };
            return (int)field >= 0;
        }

        private static bool TryMapOp(string? value, out PredicateOp op)
        {
            op = value switch
            {
                "eq" => PredicateOp.Eq,
                "ne" => PredicateOp.Ne,
                "lt" => PredicateOp.Lt,
                "le" => PredicateOp.Le,
                "gt" => PredicateOp.Gt,
                "ge" => PredicateOp.Ge,
                "between" => PredicateOp.Between,
                "regex" => PredicateOp.Regex,
                _ => (PredicateOp)(-1),
            };
            return (int)op >= 0;
        }

        private sealed class ProgramDto
        {
            [JsonPropertyName("reasoning")]
            public string? Reasoning { get; set; }

            [JsonPropertyName("supported")]
            public bool? Supported { get; set; }

            [JsonPropertyName("unsupported_reason")]
            public string? UnsupportedReason { get; set; }

            [JsonPropertyName("target")]
            public string? Target { get; set; }

            [JsonPropertyName("node_filter")]
            public NodeFilterDto? NodeFilter { get; set; }

            [JsonPropertyName("paragraph_filter")]
            public ParagraphFilterDto? ParagraphFilter { get; set; }

            [JsonPropertyName("transform")]
            public TransformDto? Transform { get; set; }
        }

        private sealed class NodeFilterDto
        {
            [JsonPropertyName("ordinal_from")]
            public int? OrdinalFrom { get; set; }

            [JsonPropertyName("ordinal_to")]
            public int? OrdinalTo { get; set; }

            [JsonPropertyName("title_regex")]
            public string? TitleRegex { get; set; }
        }

        private sealed class ParagraphFilterDto
        {
            [JsonPropertyName("where")]
            public List<PredicateDto>? Where { get; set; }
        }

        private sealed class PredicateDto
        {
            [JsonPropertyName("field")]
            public string? Field { get; set; }

            [JsonPropertyName("op")]
            public string? Op { get; set; }

            [JsonPropertyName("value")]
            public int? Value { get; set; }

            [JsonPropertyName("value_to")]
            public int? ValueTo { get; set; }

            [JsonPropertyName("regex")]
            public string? Regex { get; set; }
        }

        private sealed class TransformDto
        {
            [JsonPropertyName("kind")]
            public string? Kind { get; set; }

            [JsonPropertyName("pattern")]
            public string? Pattern { get; set; }

            [JsonPropertyName("replacement")]
            public string? Replacement { get; set; }

            [JsonPropertyName("template")]
            public string? Template { get; set; }

            [JsonPropertyName("instruction")]
            public string? Instruction { get; set; }
        }
    }
}
