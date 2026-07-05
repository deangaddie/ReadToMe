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

        public const int DefaultContextParagraphsBefore = 6;
        public const int DefaultContextParagraphsAfter = 4;

        public const string DefaultCharacterPrompt =
            """
            You are an audiobook script classifier for the book "{{book_title}}" by {{book_author}}.

            For the paragraph containing dialog, return the name of the character speaking.

            Use the paragraph itself first; if unclear, use context from surrounding paragraphs and the known characters list.

            How to identify the speaker:
            - Attribution tags in or around the quote ("said X", "X replied") — these often
              appear AFTER the quote, or in a neighbouring paragraph.
            - Vocatives: a character addressed by name inside the quote ("Well, John?") is
              usually NOT the speaker; the character who replies next often is.
            - Two-person conversations normally alternate speakers — use the speakers of
              surrounding attributed paragraphs to infer the pattern.
            - Match epithets and descriptions ("the old man", "his mother") to the known
              characters list, including aliases.
            - Content clues: what is said, who would know it, and each character's manner
              of speaking.

            Rules:
            - A paragraph has at most one speaking character.
            - First write one short sentence in "reasoning" explaining who speaks and why,
              then give the answer.
            - Never return pronouns (he, she, they) as the speaker — resolve them to a
              known character.
            - Return "unknown" only after using ALL of the above and finding that two or
              more characters remain equally plausible, or the speaker genuinely never
              appears in the text.
            - Return ONLY valid JSON. No markdown fences, no text outside the JSON.
            - JSON format: {{response_format}}

            Known characters (JSON array; each entry has a "name" and optional "aliases" — match either when identifying the speaker): {{known_characters}}

            Context paragraphs (JSON object):
            - "preceding": paragraphs before the target, in order
            - "query": the paragraph to attribute — determine who speaks this
            - "following": paragraphs after the target, in order
            - "speaker" (when present) is the already-known speaker for that paragraph

            {{context_json}}
            """;

        public const string DefaultBatchCharacterPrompt =
            """
            You are an audiobook script classifier for the book "{{book_title}}" by {{book_author}}.

            Several paragraphs containing dialog need a speaker. Each is marked with an "index".
            For every indexed paragraph, return the name of the character speaking.

            Use each paragraph itself first; if unclear, use the surrounding paragraphs (their
            "speaker" is already known) and the known characters list.

            How to identify each speaker:
            - Attribution tags in or around the quote ("said X", "X replied") — these often
              appear AFTER the quote, or in a neighbouring paragraph.
            - Vocatives: a character addressed by name inside the quote ("Well, John?") is
              usually NOT the speaker; the character who replies next often is.
            - Two-person conversations normally alternate speakers — use the speakers of
              surrounding attributed paragraphs to infer the pattern.
            - Match epithets and descriptions ("the old man", "his mother") to the known
              characters list, including aliases.
            - Content clues: what is said, who would know it, and each character's manner
              of speaking.

            Rules:
            - A paragraph has at most one speaking character.
            - For each index, first write one short sentence in "reasoning" explaining who
              speaks and why, then give the answer.
            - Never return pronouns (he, she, they) as a speaker — resolve them to a
              known character.
            - Return "unknown" for an index only after using ALL of the above and finding
              that two or more characters remain equally plausible, or the speaker genuinely
              never appears in the text.
            - Return ONLY a valid JSON array with exactly one entry per index. Every index must appear. No markdown fences, no text outside the JSON.
            - JSON format: {{response_format}}

            Known characters (JSON array; each entry has a "name" and optional "aliases" — match either when identifying a speaker): {{known_characters}}

            Paragraphs (JSON object): "paragraphs" is the passage in reading order. Entries with an
            "index" are the ones to attribute; entries with a "speaker" are context.

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

            Rules:
            - One voice must serve the entire book. If the character ages or changes over
              the story, choose a single age and delivery that fits how the character
              speaks for the majority of the book — never a voice tied to one scene or to
              only the character's youngest or oldest moments.
            - Prefer stable qualities (pitch, timbre, accent, pace) over transient emotion.
            """;

        public const string DefaultVoicePlanPrompt =
            """
            You are casting speaking voices for the character "{{character_name}}" in the
            audiobook "{{book_title}}" by {{book_author}}.

            Decide how many distinct voices this character needs across the whole book.
            Most characters need exactly one voice. Add more only when the character's
            voice genuinely changes during the story — large time skips, ageing from
            child to adult, disguise, transformation.

            For each voice return:
            - "name": a short descriptive name (e.g. "Young Pip", "Adult Pip").
            - "description": a full description of the voice. If you can determine where
              in the book the voice applies, include the from/to range (e.g. "Part 1,
              Chapter 1 to Part 2, Chapter 3"). If the character needs only one voice,
              state that it covers the whole book.
            - "design_prompt": a concise prompt that can be passed to a voice-design
              model to synthesise the voice. Describe age, gender, accent, timbre, pace
              and emotional default in one paragraph. Prefer stable qualities (pitch,
              timbre, accent, pace) over transient emotion.

            Rules:
            - Return ONLY a valid JSON array with one entry per voice. No markdown
              fences, no text outside the JSON.
            - JSON format: {{response_format}}
            """;

        public const string DefaultNarratorVoicePlanPrompt =
            """
            You are casting the NARRATION voice for the audiobook "{{book_title}}" by
            {{book_author}}. "{{character_name}}" is the narrator, not a character in the
            story.

            The narrator reads all the prose — description, action, and dialogue tags —
            in a single steady voice. This is NOT a character voice: it must not imitate
            any character, accent, gender, age or emotion belonging to the people in the
            book. Return exactly one voice unless the book genuinely switches narrator
            (e.g. a framed story with a different first-person narrator per part).

            For each voice return:
            - "name": a short descriptive name (e.g. "Narrator").
            - "description": a full description of the narration voice. State the range it
              covers — for a single narrator, say it covers the whole book.
            - "design_prompt": a concise prompt that can be passed to a voice-design model
              to synthesise the voice. Describe a clear, neutral, engaging reading voice —
              age, gender, accent, timbre, pace and a calm, even emotional default suited
              to sustained narration. Prefer stable qualities (pitch, timbre, accent, pace)
              over transient emotion.

            Rules:
            - Design ONE narration voice, not a set of character voices. Never return a
              separate voice per character or per line of dialogue.
            - Return ONLY a valid JSON array with one entry per voice. No markdown
              fences, no text outside the JSON.
            - JSON format: {{response_format}}
            """;

        /// <summary>
        /// A neutral voice-test sentence used as the sample text sent to the voice-design AI.
        /// Stored as the voice transcript for generated voices.
        /// </summary>
        public const string VoiceDesignSampleSentence =
            "The morning light filtered through tall oak trees as Sarah walked along the winding path. " +
            "She had lived in this valley all her life, yet each season brought something new to discover. " +
            "A soft breeze carried the scent of pine and rain, and somewhere in the distance a hawk cried out.";

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

        public static string BuildBatchContextJson(ParagraphBatchContext ctx)
        {
            var obj = new BatchContextJsonDto(
                [.. ctx.Entries.Select(e => new BatchEntryDto(e.TargetIndex, e.Text, e.Speaker))]);
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

        private sealed record BatchEntryDto(
            [property: JsonPropertyName("index")] int? Index,
            [property: JsonPropertyName("paragraph")] string Paragraph,
            [property: JsonPropertyName("speaker")] string? Speaker);

        private sealed record BatchContextJsonDto(
            [property: JsonPropertyName("paragraphs")] BatchEntryDto[] Paragraphs);

        private sealed record ContextJsonDto(
            [property: JsonPropertyName("preceding")] ContextEntryDto[] Preceding,
            [property: JsonPropertyName("query")] ContextEntryDto Query,
            [property: JsonPropertyName("following")] ContextEntryDto[] Following);
    }
}
