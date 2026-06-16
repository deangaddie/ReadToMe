using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Built-in default prompt templates and minimal {{token}} substitution.
    /// NOT a Handlebars engine — exact "{{name}}" literals are replaced, nothing else.
    /// </summary>
    public static class PromptTemplates
    {
        public const string BookTitle = "book_title";
        public const string BookAuthor = "book_author";
        public const string KnownCharacters = "known_characters";
        public const string ContextJson = "context_json";
        public const string ResponseFormat = "response_format";
        public const string CharacterName = "character_name";

        public const int DefaultContextParagraphsBefore = 4;
        public const int DefaultContextParagraphsAfter = 2;

        public const string DefaultCharacterPrompt =
            """
            You are an audiobook script classifier for the book "{{book_title}}" by {{book_author}}.

            For the paragraph containing dialog, return the name of the character speaking.

            Use the paragraph itself first; if unclear, use context from surrounding paragraphs and the known characters list.

            Rules:
            - A paragraph has at most one speaking character.
            - If the speaker is not clearly identifiable, return "unknown".
            - Do not return pronouns (he, she, they) as the speaker; if you cannot work out the speaker, return "unknown".
            - Return ONLY valid JSON. No markdown fences, no explanation.
            - JSON format: {{response_format}}

            Known characters (JSON array of names): {{known_characters}}

            Context paragraphs (JSON object):
            - "preceding": paragraphs before the target, in order
            - "query": the paragraph to attribute — determine who speaks this
            - "following": paragraphs after the target, in order
            - "speaker" (when present) is the already-known speaker for that paragraph

            {{context_json}}
            """;

        public const string DefaultVoicePrompt =
            """
            You are designing a distinctive speaking voice for a character in the
            audiobook "{{book_title}}" by {{book_author}}.

            Character: {{character_name}}

            Write a single concise prompt (plain text, no JSON, no preamble) that can be
            passed to a voice-design model to synthesise this character's voice. Describe
            age, gender, accent, timbre, pace and emotional default in one paragraph.
            """;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string BuildContextJson(ParagraphContext ctx)
        {
            var obj = new ContextJsonDto(
                ctx.Preceding.Count > 0
                    ? [.. ctx.Preceding.Select(p => new ContextEntryDto(p.Text, p.Speaker))]
                    : [],
                new ContextEntryDto(ctx.Query.Text, ctx.Query.Speaker),
                ctx.Following.Count > 0
                    ? [.. ctx.Following.Select(p => new ContextEntryDto(p.Text, p.Speaker))]
                    : []
            );
            return JsonSerializer.Serialize(obj, _jsonOptions);
        }

        /// <summary>
        /// Replaces each "{{key}}" literal with values[key]. Unknown tokens are left intact.
        /// Case-sensitive. No nesting, no logic, no escaping.
        /// </summary>
        public static string Render(string template, IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(template)) return template ?? string.Empty;
            var sb = new StringBuilder(template);
            foreach (var (key, value) in values)
                sb.Replace("{{" + key + "}}", value ?? string.Empty);
            return sb.ToString();
        }

        private sealed record ContextEntryDto(
            [property: JsonPropertyName("paragraph")] string Paragraph,
            [property: JsonPropertyName("speaker")] string? Speaker);

        private sealed record ContextJsonDto(
            [property: JsonPropertyName("preceding")] ContextEntryDto[] Preceding,
            [property: JsonPropertyName("query")] ContextEntryDto Query,
            [property: JsonPropertyName("following")] ContextEntryDto[] Following);
    }
}
