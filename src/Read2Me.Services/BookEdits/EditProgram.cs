namespace Read2Me.Services.BookEdits
{
    public enum EditTargetSelector { VolumeTitle, PartTitle, ChapterTitle, ParagraphText }

    public enum TransformKind { RegexReplace, SetTemplate, Llm }

    public enum PredicateField { ParagraphOrdinal, ParagraphOrdinalFromEnd, ItemOrdinal, Text }

    public enum PredicateOp { Eq, Ne, Lt, Le, Gt, Ge, Between, Regex }

    /// <summary>Filters nodes at the target level (or the containing chapter when the
    /// target is paragraph text). Null fields mean "no filter". Ordinals are 1-based
    /// inclusive, counted book-wide in reading order.</summary>
    public sealed record NodeFilter(int? OrdinalFrom, int? OrdinalTo, string? TitleRegex)
    {
        public static readonly NodeFilter All = new(null, null, null);
    }

    /// <summary>One condition over a paragraph text item. Ordinal fields are 1-based:
    /// ParagraphOrdinal counts content paragraphs within their chapter,
    /// ParagraphOrdinalFromEnd counts backwards (1 = last), ItemOrdinal counts text
    /// items within their paragraph. Text uses the Regex op against the item's text.</summary>
    public sealed record EditPredicate(
        PredicateField Field,
        PredicateOp Op,
        int? Value = null,
        int? ValueTo = null,
        string? Regex = null);

    /// <summary>Selects text items inside matched chapters; only meaningful when the
    /// target is paragraph text. All predicates must hold (AND); an empty list matches
    /// every content item.</summary>
    public sealed record ParagraphFilter(IReadOnlyList<EditPredicate> Where)
    {
        public static readonly ParagraphFilter All = new([]);
    }

    public sealed record EditTransform(
        TransformKind Kind,
        string? Pattern = null,
        string? Replacement = null,
        string? Template = null,
        string? Instruction = null);

    /// <summary>Structured edit plan produced by the phase-A LLM call from the user's
    /// free-text instruction. Keep in sync with EditProgramSchema below.</summary>
    public sealed record EditProgram(
        bool Supported,
        string? UnsupportedReason,
        EditTargetSelector Target,
        NodeFilter NodeFilter,
        ParagraphFilter ParagraphFilter,
        EditTransform Transform,
        string? Reasoning = null);

    public static class EditProgramSchema
    {
        /// <summary>Injected into the prompt via {{response_format}}.</summary>
        public const string JsonExample =
            "{ \"reasoning\": \"user wants every chapter renamed with a number prefix\", \"supported\": true, \"unsupported_reason\": null, " +
            "\"target\": \"chapter_title\", " +
            "\"node_filter\": { \"ordinal_from\": null, \"ordinal_to\": null, \"title_regex\": null }, " +
            "\"paragraph_filter\": { \"where\": [] }, " +
            "\"transform\": { \"kind\": \"set_template\", \"pattern\": null, \"replacement\": null, \"template\": \"Chapter {n}: {old}\", \"instruction\": null } }";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Property order matters: reasoning first so the model reasons before answering.
        /// Keep in sync with JsonExample above.
        /// </summary>
        public const string JsonSchema = """
            {
              "type": "object",
              "properties": {
                "reasoning": { "type": "string" },
                "supported": { "type": "boolean" },
                "unsupported_reason": { "type": ["string", "null"] },
                "target": { "type": "string", "enum": ["volume_title", "part_title", "chapter_title", "paragraph_text"] },
                "node_filter": {
                  "type": "object",
                  "properties": {
                    "ordinal_from": { "type": ["integer", "null"] },
                    "ordinal_to": { "type": ["integer", "null"] },
                    "title_regex": { "type": ["string", "null"] }
                  },
                  "required": ["ordinal_from", "ordinal_to", "title_regex"]
                },
                "paragraph_filter": {
                  "type": "object",
                  "properties": {
                    "where": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "field": { "type": "string", "enum": ["paragraph_ordinal", "paragraph_ordinal_from_end", "item_ordinal", "text"] },
                          "op": { "type": "string", "enum": ["eq", "ne", "lt", "le", "gt", "ge", "between", "regex"] },
                          "value": { "type": ["integer", "null"] },
                          "value_to": { "type": ["integer", "null"] },
                          "regex": { "type": ["string", "null"] }
                        },
                        "required": ["field", "op", "value", "value_to", "regex"]
                      }
                    }
                  },
                  "required": ["where"]
                },
                "transform": {
                  "type": "object",
                  "properties": {
                    "kind": { "type": "string", "enum": ["regex_replace", "set_template", "llm"] },
                    "pattern": { "type": ["string", "null"] },
                    "replacement": { "type": ["string", "null"] },
                    "template": { "type": ["string", "null"] },
                    "instruction": { "type": ["string", "null"] }
                  },
                  "required": ["kind", "pattern", "replacement", "template", "instruction"]
                }
              },
              "required": ["reasoning", "supported", "unsupported_reason", "target", "node_filter", "paragraph_filter", "transform"]
            }
            """;
    }

    public static class BookEditBatchSchema
    {
        /// <summary>Injected into the batch prompt via {{response_format}}.</summary>
        public const string JsonExample =
            "[ { \"index\": 0, \"reasoning\": \"text starts mid-word; missing letter is I\", \"new_text\": \"It is a truth universally acknowledged...\" } ]";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Keep in sync with JsonExample above.
        /// </summary>
        public const string JsonSchema = """
            {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "index": { "type": "integer" },
                  "reasoning": { "type": "string" },
                  "new_text": { "type": "string" }
                },
                "required": ["index", "reasoning", "new_text"]
              }
            }
            """;
    }
}
