using Read2Me.Services.Characters;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The Pride and Prejudice sample values a prompt template is previewed with. Shared by the preview endpoint
    /// (<c>POST /api/settings/prompts/{kind}/preview</c>) and the catalog, so
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
            new PromptTemplates.RosterCharacter("Elizabeth", ["Lizzy", "Miss Eliza"]),
            new PromptTemplates.RosterCharacter("Darcy", ["Mr. Darcy"]),
            new PromptTemplates.RosterCharacter("Bingley", ["the new tenant"]),
        ]);

        private static ContextItem Narration(string text) =>
            new(Guid.Empty, text, AttributionWire.Narration, AttributionWire.Narrator);

        private static ContextItem Dialog(string text, string speaker) =>
            new(Guid.Empty, text, AttributionWire.Dialog, speaker);

        private static ContextParagraph Paragraph(params ContextItem[] items) =>
            new(string.Concat(items.Select(i => i.Text)), items);

        private static readonly ContextParagraph SampleOpening =
            Paragraph(Narration("It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife."));

        private static readonly ContextParagraph SampleDarcy =
            Paragraph(Dialog("\"She is tolerable, but not handsome enough to tempt me,\"", "Darcy"),
                      Narration(" said Mr. Darcy."));

        private static readonly ContextParagraph SampleQuery =
            Paragraph(Dialog("\"I could easily forgive his pride, if he had not mortified mine,\"", AttributionWire.Unknown),
                      Narration(" she replied."));

        private static readonly ContextParagraph SampleWindow =
            Paragraph(Narration("She paused at the window. The carriage was already at the door."));

        private static readonly ContextParagraph SampleSettled =
            Paragraph(Dialog("\"Then it is settled,\"", AttributionWire.Unknown),
                      Narration(" said her father."));

        private static readonly ParagraphContext SampleContext =
            new(SampleQuery, [SampleOpening, SampleDarcy], [SampleWindow]);

        private static readonly ParagraphBatchContext SampleBatchContext =
            new([
                new BatchContextEntry(SampleOpening.Text, SampleOpening.Items, null),
                new BatchContextEntry(SampleDarcy.Text, SampleDarcy.Items, null),
                new BatchContextEntry(SampleQuery.Text, SampleQuery.Items, 0),
                new BatchContextEntry(SampleWindow.Text, SampleWindow.Items, null),
                new BatchContextEntry(SampleSettled.Text, SampleSettled.Items, 1),
            ], [], []);

        /// <summary>Single-paragraph attribution (Full and Simple) — also the voice prompt's values.</summary>
        public static IReadOnlyDictionary<string, string> Character { get; } =
            new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle] = "Pride and Prejudice",
                [PromptTemplates.BookAuthor] = "Jane Austen",
                [PromptTemplates.KnownCharacters] = SampleRoster,
                [PromptTemplates.ContextJson] = PromptTemplates.BuildContextJson(SampleContext),
                [PromptTemplates.CharacterName] = "Darcy",
                [PromptTemplates.ResponseFormat] = ItemAttributionSchema.JsonExample,
                [PromptTemplates.NarratorIdentity] = NarratorPromptText.IdentityParagraph("Elizabeth"),
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
                    Chapter 1: Chapter I
                    Chapter 2: Chapter II
                    Chapter 3: Chapter III
                    """,
                [PromptTemplates.ResponseFormat] = CharacterDiscoverySchema.JsonExample,
            };
    }
}
