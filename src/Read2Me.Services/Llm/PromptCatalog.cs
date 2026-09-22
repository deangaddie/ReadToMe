using Read2Me.Services.Characters;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Everything a UI or the agent API needs to describe and edit one prompt kind: its wire name,
    /// copy, the tokens it may use, the expected response shape, the built-in default, the sample
    /// values it is previewed with, and the <see cref="LlmPromptService"/> operations behind it.
    /// </summary>
    /// <param name="MissingNarratorIdentity">
    /// Reads this kind's compatibility warning, for the two Full attribution kinds whose stored
    /// override can predate <c>{{narrator_identity}}</c>; null for kinds that carry no warning.
    /// </param>
    public sealed record PromptKindDescriptor(
        string Kind,
        string Title,
        string Description,
        IReadOnlyList<string> Tokens,
        string? ExpectedResponse,
        string DefaultTemplate,
        IReadOnlyDictionary<string, string> SampleValues,
        Func<LlmPromptService, Task<string>> Get,
        Func<LlmPromptService, string, Task> Set,
        Func<LlmPromptService, Task> Reset,
        Func<AttributionPromptCompatibility, bool>? MissingNarratorIdentity = null)
    {
        /// <summary>The compatibility problems both settings pages show for this kind, as user-facing text.</summary>
        public IReadOnlyList<string> Warnings(AttributionPromptCompatibility compatibility) =>
            MissingNarratorIdentity?.Invoke(compatibility) == true
                ? [PromptCatalog.MissingNarratorIdentityWarning]
                : [];
    }

    /// <summary>The eight editable prompt kinds, in the order the settings pages show them.</summary>
    public static class PromptCatalog
    {
        public static readonly string MissingNarratorIdentityWarning =
            "Stored override missing {{" + PromptTemplates.NarratorIdentity + "}}";

        private static readonly string[] CharacterTokens =
        [
            PromptTemplates.BookTitle,
            PromptTemplates.BookAuthor,
            PromptTemplates.KnownCharacters,
            PromptTemplates.ContextJson,
            PromptTemplates.ResponseFormat,
        ];

        private static readonly string[] FullCharacterTokens = [.. CharacterTokens, PromptTemplates.NarratorIdentity];

        private static readonly string[] VoiceTokens =
        [
            PromptTemplates.BookTitle,
            PromptTemplates.BookAuthor,
            PromptTemplates.CharacterName,
        ];

        private static readonly string[] VoicePlanTokens = [.. VoiceTokens, PromptTemplates.ResponseFormat];

        private static readonly string[] CharacterVoicePlanTokens = [.. VoicePlanTokens, PromptTemplates.AlsoNarrates];

        private static readonly string[] DiscoverCharactersTokens =
        [
            PromptTemplates.BookTitle,
            PromptTemplates.BookAuthor,
            PromptTemplates.BookOutline,
            PromptTemplates.KnownCharacters,
            PromptTemplates.ResponseFormat,
        ];

        public static IReadOnlyList<PromptKindDescriptor> Kinds { get; } =
        [
            new("character", "Book Character Prompt",
                "Sent to the LLM to identify which character speaks each item of a paragraph, and how the line should be voiced.",
                FullCharacterTokens, ItemAttributionSchema.JsonExample, PromptTemplates.DefaultCharacterPrompt,
                PromptPreviewSamples.Character,
                s => s.GetCharacterPromptAsync(AttributionPromptStyle.Full),
                (s, t) => s.SetCharacterPromptAsync(t), s => s.ResetCharacterPromptAsync(),
                c => c.CharacterPromptMissingNarratorIdentity),
            new("batch-character", "Batch Character Prompt",
                "Used instead of the single-paragraph prompt when the active LLM server's \"Paragraphs per request\" is greater than 1. Attributes several indexed paragraphs in one request.",
                FullCharacterTokens, ItemBatchAttributionSchema.JsonExample, PromptTemplates.DefaultBatchCharacterPrompt,
                PromptPreviewSamples.BatchCharacter,
                s => s.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full),
                (s, t) => s.SetBatchCharacterPromptAsync(t), s => s.ResetBatchCharacterPromptAsync(),
                c => c.BatchCharacterPromptMissingNarratorIdentity),
            new("simple-character", "Simple Character Prompt",
                "Used instead of the Book Character Prompt when the LLM server's prompt style is \"Simple\". Assigns a speaker only when the text explicitly names one, so small models answer \"unknown\" instead of guessing and the escalation chain hands the paragraph to a larger model.",
                CharacterTokens, ItemAttributionSchema.JsonExample, PromptTemplates.DefaultSimpleCharacterPrompt,
                PromptPreviewSamples.Character,
                s => s.GetCharacterPromptAsync(AttributionPromptStyle.Simple),
                (s, t) => s.SetSimpleCharacterPromptAsync(t), s => s.ResetSimpleCharacterPromptAsync()),
            new("simple-batch-character", "Simple Batch Character Prompt",
                "The batch form of the Simple Character Prompt. Used when a \"Simple\" LLM server's \"Paragraphs per request\" is greater than 1.",
                CharacterTokens, ItemBatchAttributionSchema.JsonExample, PromptTemplates.DefaultSimpleBatchCharacterPrompt,
                PromptPreviewSamples.BatchCharacter,
                s => s.GetBatchCharacterPromptAsync(AttributionPromptStyle.Simple),
                (s, t) => s.SetSimpleBatchCharacterPromptAsync(t), s => s.ResetSimpleBatchCharacterPromptAsync()),
            new("voice-plan", "Character Voice Plan Prompt",
                "Sent to the LLM by \"Generate voice prompts\" for each character without voices. Expects a JSON array of all the voices the character needs — each with a name, a description (including where in the book it applies) and a voice-design prompt.",
                CharacterVoicePlanTokens, VoicePlanSchema.JsonExample, PromptTemplates.DefaultVoicePlanPrompt,
                PromptPreviewSamples.VoicePlan,
                s => s.GetVoicePlanPromptAsync(),
                (s, t) => s.SetVoicePlanPromptAsync(t), s => s.ResetVoicePlanPromptAsync()),
            new("narrator-voice-plan", "Narrator Voice Plan Prompt",
                "Used instead of the character voice-plan prompt when \"Generate voice prompts\" runs for the narrator. Asks for one steady narration voice, never a set of per-character voices. Expects the same JSON array shape.",
                VoicePlanTokens, VoicePlanSchema.JsonExample, PromptTemplates.DefaultNarratorVoicePlanPrompt,
                PromptPreviewSamples.NarratorVoicePlan,
                s => s.GetNarratorVoicePlanPromptAsync(),
                (s, t) => s.SetNarratorVoicePlanPromptAsync(t), s => s.ResetNarratorVoicePlanPromptAsync()),
            new("discover-characters", "Discover Characters Prompt",
                "Sent to the default LLM by \"Discover characters\" on the Characters tab. Asks for the book's notable characters and their aliases as a JSON list, using the title, author and chapter outline.",
                DiscoverCharactersTokens, CharacterDiscoverySchema.JsonExample, PromptTemplates.DefaultDiscoverCharactersPrompt,
                PromptPreviewSamples.DiscoverCharacters,
                s => s.GetDiscoverCharactersPromptAsync(),
                (s, t) => s.SetDiscoverCharactersPromptAsync(t), s => s.ResetDiscoverCharactersPromptAsync()),
            new("voice", "Character Voice Prompt",
                "Sent to the LLM to generate a voice-design prompt for a single existing voice (per-voice regenerate on the character detail panel). Expects a single plain-text response.",
                VoiceTokens, null, PromptTemplates.DefaultVoicePrompt,
                PromptPreviewSamples.Voice,
                s => s.GetVoicePromptAsync(),
                (s, t) => s.SetVoicePromptAsync(t), s => s.ResetVoicePromptAsync()),
        ];

        /// <summary>Exact (case-sensitive) wire-name lookup; null for an unknown kind.</summary>
        public static PromptKindDescriptor? Find(string kind) =>
            Kinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.Ordinal));
    }
}
