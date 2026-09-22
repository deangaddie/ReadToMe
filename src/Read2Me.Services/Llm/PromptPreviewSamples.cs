using Read2Me.Services.Characters;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The Hobbit sample values a prompt template is previewed with. Shared by the Blazor page,
    /// the Angular page (over <c>POST /api/settings/prompts/{kind}/preview</c>) and the catalog, so
    /// every preview shows the same book.
    /// </summary>
    /// <remarks>
    /// The roster and <c>{{context_json}}</c> samples are serialised by the builders the real
    /// request uses, so a change to the wire shape cannot leave a stale example behind. Only the
    /// book text is invented; item ids never reach the model.
    /// </remarks>
    public static class PromptPreviewSamples
    {
        private static readonly string SampleRoster = PromptTemplates.BuildKnownCharactersJson([
            new PromptTemplates.RosterCharacter("Bilbo", ["Mr. Baggins", "the hobbit"]),
            new PromptTemplates.RosterCharacter("Gandalf", ["the wizard"]),
            new PromptTemplates.RosterCharacter("Thorin", []),
        ]);

        private static ContextItem Narration(string text) =>
            new(Guid.Empty, text, AttributionWire.Narration, AttributionWire.Narrator);

        private static ContextItem Dialog(string text, string speaker) =>
            new(Guid.Empty, text, AttributionWire.Dialog, speaker);

        private static ContextParagraph Paragraph(params ContextItem[] items) =>
            new(string.Concat(items.Select(i => i.Text)), items);

        private static readonly ContextParagraph SampleRoad =
            Paragraph(Narration("The road goes ever on and on, down from the door where it began."));

        private static readonly ContextParagraph SampleGandalf =
            Paragraph(Dialog("\"You are a very fine person, Mr. Baggins,\"", "Gandalf"),
                      Narration(" said Gandalf."));

        private static readonly ContextParagraph SampleQuery =
            Paragraph(Dialog("\"I'm going on an adventure!\"", AttributionWire.Unknown),
                      Narration(" he cried."));

        private static readonly ContextParagraph SampleWestering =
            Paragraph(Narration("He paused and looked back. The sun was already westering."));

        private static readonly ContextParagraph SampleSettled =
            Paragraph(Dialog("\"Then it is settled,\"", AttributionWire.Unknown),
                      Narration(" said the dwarf."));

        private static readonly ParagraphContext SampleContext =
            new(SampleQuery, [SampleRoad, SampleGandalf], [SampleWestering]);

        private static readonly ParagraphBatchContext SampleBatchContext =
            new([
                new BatchContextEntry(SampleRoad.Text, SampleRoad.Items, null),
                new BatchContextEntry(SampleGandalf.Text, SampleGandalf.Items, null),
                new BatchContextEntry(SampleQuery.Text, SampleQuery.Items, 0),
                new BatchContextEntry(SampleWestering.Text, SampleWestering.Items, null),
                new BatchContextEntry(SampleSettled.Text, SampleSettled.Items, 1),
            ], [], []);

        /// <summary>Single-paragraph attribution (Full and Simple) — also the voice prompt's values.</summary>
        public static IReadOnlyDictionary<string, string> Character { get; } =
            new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle] = "The Hobbit",
                [PromptTemplates.BookAuthor] = "J.R.R. Tolkien",
                [PromptTemplates.KnownCharacters] = SampleRoster,
                [PromptTemplates.ContextJson] = PromptTemplates.BuildContextJson(SampleContext),
                [PromptTemplates.CharacterName] = "Gandalf",
                [PromptTemplates.ResponseFormat] = ItemAttributionSchema.JsonExample,
                [PromptTemplates.NarratorIdentity] = NarratorPromptText.IdentityParagraph("Bilbo"),
            };

        /// <summary>Batch attribution (Full and Simple).</summary>
        public static IReadOnlyDictionary<string, string> BatchCharacter { get; } =
            new Dictionary<string, string>(Character)
            {
                [PromptTemplates.ContextJson] = PromptTemplates.BuildBatchContextJson(SampleBatchContext),
                [PromptTemplates.ResponseFormat] = ItemBatchAttributionSchema.JsonExample,
            };

        public static IReadOnlyDictionary<string, string> Voice => Character;

        public static IReadOnlyDictionary<string, string> VoicePlan { get; } =
            new Dictionary<string, string>(Character)
            {
                [PromptTemplates.ResponseFormat] = VoicePlanSchema.JsonExample,
                [PromptTemplates.AlsoNarrates] = NarratorPromptText.AlsoNarratesParagraph,
            };

        public static IReadOnlyDictionary<string, string> NarratorVoicePlan { get; } =
            new Dictionary<string, string>(VoicePlan)
            {
                [PromptTemplates.CharacterName] = "Narrator",
            };

        public static IReadOnlyDictionary<string, string> DiscoverCharacters { get; } =
            new Dictionary<string, string>(Character)
            {
                [PromptTemplates.BookOutline] = """
                    1 volume(s), 1 part(s), 3 chapter(s).
                    Chapter 1: An Unexpected Party
                    Chapter 2: Roast Mutton
                    Chapter 3: A Short Rest
                    """,
                [PromptTemplates.ResponseFormat] = CharacterDiscoverySchema.JsonExample,
            };
    }
}
