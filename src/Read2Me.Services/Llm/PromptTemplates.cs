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
        public const string Instruction = "instruction";
        public const string BookOutline = "book_outline";
        public const string EditItemsJson = "edit_items_json";

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

        public const string DefaultSimpleCharacterPrompt =
            """
            You are an audiobook script classifier for the book "{{book_title}}" by {{book_author}}.

            For the paragraph containing dialog, return the name of the character speaking —
            but ONLY if the text explicitly states who speaks.

            The ONLY acceptable evidence is an attribution tag that names the speaker of the
            query paragraph: "said X", "X replied", "asked X", "X went on". The tag may sit
            before, inside, or after the quote in the query paragraph itself, or in the
            paragraph immediately before or after it when it clearly refers to the query
            paragraph's quote.

            Do NOT infer the speaker any other way. Do not use conversation turn-taking,
            names addressed inside the quote, descriptions of who is present, or what the
            dialog says. If you would have to reason it out, the answer is "unknown".

            Rules:
            - A paragraph has at most one speaking character.
            - First write one short sentence in "reasoning" quoting the attribution tag you
              found, or stating that there is none, then give the answer.
            - Never return pronouns (he, she, they) as the speaker. If the tag uses only a
              pronoun and no sentence right next to it names that person, return "unknown".
            - When the tag names a speaker, return that character's "name" from the known
              characters list (the tag may use an alias).
            - If there is no attribution tag naming the speaker, return "unknown". "unknown"
              is a correct and expected answer — another system handles those paragraphs.
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

        public const string DefaultSimpleBatchCharacterPrompt =
            """
            You are an audiobook script classifier for the book "{{book_title}}" by {{book_author}}.

            Several paragraphs containing dialog need a speaker. Each is marked with an "index".
            For every indexed paragraph, return the name of the character speaking — but ONLY
            if the text explicitly states who speaks that paragraph.

            The ONLY acceptable evidence is an attribution tag that names the speaker of the
            indexed paragraph: "said X", "X replied", "asked X", "X went on". The tag may sit
            before, inside, or after the quote in the indexed paragraph itself, or in the
            paragraph immediately before or after it when it clearly refers to that quote.

            Do NOT infer a speaker any other way. Do not use conversation turn-taking, names
            addressed inside a quote, descriptions of who is present, or what the dialog says.
            If you would have to reason it out, the answer for that index is "unknown".

            Rules:
            - A paragraph has at most one speaking character.
            - For each index, first write one short sentence in "reasoning" quoting the
              attribution tag you found, or stating that there is none, then give the answer.
            - Never return pronouns (he, she, they) as a speaker. If the tag uses only a
              pronoun and no sentence right next to it names that person, return "unknown".
            - When a tag names a speaker, return that character's "name" from the known
              characters list (the tag may use an alias).
            - If an indexed paragraph has no attribution tag naming its speaker, return
              "unknown" for that index. "unknown" is a correct and expected answer — another
              system handles those paragraphs.
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

        public const string DefaultDiscoverCharactersPrompt =
            """
            You are cataloguing the notable characters of the audiobook "{{book_title}}" by {{book_author}}.

            List the characters a listener would recognise as distinct speaking or named
            people in this book — protagonists, antagonists, and significant supporting
            characters. Use your knowledge of this published work together with the chapter
            outline below. Do not invent characters that do not belong to this book, and do
            not include the narrator.

            For each character return:
            - "name": the character's primary name, as a listener would most naturally refer
              to them (e.g. "Bilbo", not "Mr. Bilbo Baggins").
            - "aliases": every other name, title, epithet or nickname the text uses for that
              same character (e.g. "Mr. Baggins", "the hobbit"). Use an empty list if there
              are none.

            The characters already in the project are listed below — you may still include
            them (they will be recognised and not duplicated), but spend your effort on the
            ones that are missing.

            Rules:
            - First write one short sentence in "reasoning" explaining how you identified the cast.
            - Return ONLY valid JSON. No markdown fences, no text outside the JSON.
            - JSON format: {{response_format}}

            Book outline:
            {{book_outline}}

            Characters already in the project (JSON array; each entry has a "name" and optional "aliases"): {{known_characters}}
            """;

        public const string DefaultEditPlanPrompt =
            """
            You are an editing assistant for the audiobook "{{book_title}}" by {{book_author}}.
            The user wants to change the book's text or titles and has written an instruction
            in plain language. Turn that instruction into a structured edit plan.

            User instruction: {{instruction}}

            Book outline:
            {{book_outline}}

            The plan has three parts:

            1. "target" — what kind of value is edited:
               - "volume_title", "part_title", "chapter_title": rename those nodes.
               - "paragraph_text": edit the text of paragraphs.

            2. Scope filters — which nodes are edited:
               - "node_filter" narrows the nodes at the target level (for "paragraph_text"
                 it narrows the chapters that contain the paragraphs). "ordinal_from" /
                 "ordinal_to" are 1-based inclusive positions in reading order (e.g.
                 chapters 3 to 7). "title_regex" is a .NET regex matched against the node's
                 title. Use null for any filter the instruction does not mention.
               - "paragraph_filter" (only for "paragraph_text"): "where" is a list of
                 conditions, ANDed together; an empty list means every paragraph. Each
                 condition has a "field", an "op", and either an integer "value" (plus
                 "value_to" for "between") or a "regex". Fields:
                   - "paragraph_ordinal": 1-based position of the paragraph within its
                     chapter (1 = first). Example — the second paragraph of every chapter:
                     { "field": "paragraph_ordinal", "op": "eq", "value": 2 }.
                   - "paragraph_ordinal_from_end": counted backwards (1 = last paragraph).
                   - "item_ordinal": paragraphs are split into text items (narration and
                     dialogue); this is the item's 1-based position within its paragraph.
                     Add { "field": "item_ordinal", "op": "eq", "value": 1 } only when the
                     instruction targets the start of a paragraph (e.g. its opening words).
                   - "text": the item's text; must use op "regex" with a .NET "regex".
                 Ops: "eq", "ne", "lt", "le", "gt", "ge", "between" (value..value_to),
                 "regex" (text field only).
                 Never approximate a position with a text regex — use the ordinal fields.
                 If the instruction selects paragraphs in a way these fields cannot
                 express, set "supported" to false instead of guessing.

            3. "transform" — how each matched value changes. Pick exactly one kind:
               - "regex_replace": a mechanical find/replace. Set "pattern" (.NET regex) and
                 "replacement" ($1-style group references allowed).
               - "set_template": the whole value is replaced by "template". Tokens: {n} is
                 the 1-based position of the item within the matched scope, {old} is the
                 current value. Example: "Chapter {n}: {old}". Use this for renames with
                 numbering, prefixes ("X{old}") or suffixes ("{old}X").
               - "llm": the change needs understanding of the text (e.g. "restore the missing
                 first letter", "fix the grammar"). Set "instruction" to a precise, standalone
                 command that will be applied to each matched text on its own.

            If the instruction asks for anything other than editing titles or paragraph text —
            adding, deleting, splitting, merging or reordering content, changing audio or
            voices — set "supported" to false and explain in "unsupported_reason".

            Rules:
            - First write one short sentence in "reasoning" explaining how you read the instruction.
            - Use the most deterministic transform that satisfies the instruction: prefer
              set_template or regex_replace over llm when the change is mechanical.
            - Return ONLY valid JSON. No markdown fences, no text outside the JSON.
            - JSON format: {{response_format}}
            """;

        public const string DefaultBatchEditPrompt =
            """
            You are an editing assistant for the audiobook "{{book_title}}" by {{book_author}}.

            Apply the following instruction to each of the texts below, independently:

            Instruction: {{instruction}}

            Each entry has an "index", a "path" describing where the text sits in the book,
            and the current "text".

            Rules:
            - For each index, first write one short sentence in "reasoning", then return the
              complete edited text as "new_text" — the full replacement value, not a diff.
            - Change only what the instruction requires; keep everything else exactly as is.
            - If the instruction does not apply to an entry, return its text unchanged.
            - Return ONLY a valid JSON array with exactly one entry per index. Every index
              must appear. No markdown fences, no text outside the JSON.
            - JSON format: {{response_format}}

            Texts (JSON array):
            {{edit_items_json}}
            """;

        /// <summary>Serializes edit targets for the batch edit prompt.</summary>
        public static string BuildEditItemsJson(IEnumerable<(int Index, string Path, string Text)> items)
        {
            var arr = items.Select(i => new EditItemDto(i.Index, i.Path, i.Text)).ToArray();
            return JsonSerializer.Serialize(arr, _jsonOptions);
        }

        private sealed record EditItemDto(
            [property: JsonPropertyName("index")] int Index,
            [property: JsonPropertyName("path")] string Path,
            [property: JsonPropertyName("text")] string Text);

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
