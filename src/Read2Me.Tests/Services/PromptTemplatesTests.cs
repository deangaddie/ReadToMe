using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Read2Me.Services;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class PromptTemplatesTests
    {
        [Fact]
        public void Render_ReplacesKnownTokens()
        {
            var result = PromptTemplates.Render(
                "Hi {{book_title}}",
                new Dictionary<string, string> { [PromptTemplates.BookTitle] = "X" });
            Assert.Equal("Hi X", result);
        }

        [Fact]
        public void Render_LeavesUnknownTokensIntact()
        {
            var result = PromptTemplates.Render(
                "Hello {{unknown_token}}",
                new Dictionary<string, string>());
            Assert.Equal("Hello {{unknown_token}}", result);
        }

        [Fact]
        public void Render_ReplacesAllOccurrences()
        {
            var result = PromptTemplates.Render(
                "{{book_title}} and {{book_title}}",
                new Dictionary<string, string> { [PromptTemplates.BookTitle] = "Dune" });
            Assert.Equal("Dune and Dune", result);
        }

        [Fact]
        public void Render_EmptyTemplate_ReturnsEmpty()
        {
            var result = PromptTemplates.Render(
                string.Empty,
                new Dictionary<string, string> { [PromptTemplates.BookTitle] = "X" });
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void DefaultCharacterPrompt_ContainsAllDeclaredTokens()
        {
            var prompt = PromptTemplates.DefaultCharacterPrompt;
            Assert.Contains("{{" + PromptTemplates.BookTitle + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.BookAuthor + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.KnownCharacters + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.ContextJson + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.ResponseFormat + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.NarratorIdentity + "}}", prompt);
        }

        [Fact]
        // Pins the current bytes, not the pre-narrator-link bytes: the hashes were re-cut when the
        // measured abstention wording landed. What it still guards is ADR-0004's rule — unlinked
        // renders with no narrator identity spliced in.
        public void AttributionDefaults_UnlinkedRenderingMatchesGoldenBytes()
        {
            var values = new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle] = "The Book",
                [PromptTemplates.BookAuthor] = "The Author",
                [PromptTemplates.KnownCharacters] = "[]",
                [PromptTemplates.ContextJson] = "{}",
                [PromptTemplates.ResponseFormat] = "{}",
                [PromptTemplates.NarratorIdentity] = string.Empty,
            };

            Assert.Equal(
                "F11B34C1CEC96B2FEF29D94C2FAC5CD2D7237D090A3C37A9A4CD12E9B697D8C5",
                Sha256(PromptTemplates.Render(PromptTemplates.DefaultCharacterPrompt, values)));
            Assert.Equal(
                "E86F09C7560FCCC2EE857A2FF624EC8E53DFBCE4C16A095F6E093B0B1B2656FD",
                Sha256(PromptTemplates.Render(PromptTemplates.DefaultBatchCharacterPrompt, values)));
        }

        private static string Sha256(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        [Fact]
        public void DefaultVoicePrompt_ContainsCharacterNameToken()
        {
            var prompt = PromptTemplates.DefaultVoicePrompt;
            Assert.Contains("{{" + PromptTemplates.CharacterName + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.BookTitle + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.BookAuthor + "}}", prompt);
        }

        [Fact]
        public void NarratorTokens_AppearOnlyInMeasuredTemplates()
        {
            Assert.DoesNotContain("{{" + PromptTemplates.NarratorIdentity + "}}", PromptTemplates.DefaultSimpleCharacterPrompt);
            Assert.DoesNotContain("{{" + PromptTemplates.NarratorIdentity + "}}", PromptTemplates.DefaultSimpleBatchCharacterPrompt);
            Assert.Contains("{{" + PromptTemplates.AlsoNarrates + "}}", PromptTemplates.DefaultVoicePlanPrompt);
            Assert.DoesNotContain("{{" + PromptTemplates.AlsoNarrates + "}}", PromptTemplates.DefaultNarratorVoicePlanPrompt);
        }

        [Fact]
        public void DefaultVoicePrompt_ContainsWholeBookVoiceRule()
        {
            Assert.Contains("One voice must serve the entire book", PromptTemplates.DefaultVoicePrompt);
        }

        [Fact]
        public void DefaultCharacterPrompt_ContainsSegmentContractInstructions()
        {
            var prompt = PromptTemplates.DefaultCharacterPrompt;
            Assert.Contains("\"reasoning\"", prompt);
            Assert.Contains("How to identify each dialog segment's speaker", prompt);
            Assert.Contains("Vocatives", prompt);
            // Fidelity and the narration-speaker convention are what the parser/aligner rely on.
            Assert.Contains("reproduce the query paragraph EXACTLY", prompt);
            Assert.Contains("Narration segments always have speaker \"narrator\"", prompt);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FullCharacterPrompts_LicenseUnknownForUnidentifiedSpeakers(bool batch)
        {
            var prompt = batch
                ? PromptTemplates.DefaultBatchCharacterPrompt
                : PromptTemplates.DefaultCharacterPrompt;

            // The measured fix for cold-start confabulation: a roster name is only a candidate once
            // the visible text places that person here, and abstaining beats guessing.
            Assert.Contains("Who counts as a candidate speaker", prompt);
            Assert.Contains("A wrong name is worse than \"unknown\"", prompt);
        }

        [Fact]
        public void DefaultBatchCharacterPrompt_ContainsSegmentContractInstructions()
        {
            var prompt = PromptTemplates.DefaultBatchCharacterPrompt;
            Assert.Contains("\"reasoning\"", prompt);
            Assert.Contains("How to identify each dialog segment's speaker", prompt);
            Assert.Contains("Vocatives", prompt);
            Assert.Contains("paragraph EXACTLY", prompt);
            Assert.Contains("Narration segments always have speaker \"narrator\"", prompt);
            // Both trial models answered for context paragraphs unless told not to.
            Assert.Contains("Output entries ONLY for the paragraphs that have an \"index\"", prompt);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SimpleCharacterPrompts_RestrictEvidenceToAttributionTags(bool batch)
        {
            var prompt = batch
                ? PromptTemplates.DefaultSimpleBatchCharacterPrompt
                : PromptTemplates.DefaultSimpleCharacterPrompt;

            Assert.Contains("The ONLY acceptable evidence is an attribution tag", prompt);
            Assert.Contains("Do NOT infer a speaker any other way", prompt);
            // Simple shares the segment contract with standard — only the evidence policy differs.
            Assert.Contains("Narration segments always have speaker \"narrator\"", prompt);
            Assert.Contains("{{" + PromptTemplates.ContextJson + "}}", prompt);
        }

        [Fact]
        public void DefaultContextWindowConstants_AreCorrect()
        {
            Assert.Equal(6, PromptTemplates.DefaultContextParagraphsBefore);
            Assert.Equal(4, PromptTemplates.DefaultContextParagraphsAfter);
        }

        /// <summary>An item with a throwaway id — ids never reach the JSON, only the caller's map.</summary>
        private static ContextItem Item(string text, string type, string speaker) =>
            new(Guid.NewGuid(), text, type, speaker);

        [Fact]
        public void BuildContextJson_ContextParagraphs_EmitSegmentsWithSpeakers()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("Who said this?", []),
                [new ContextParagraph("\"Hello.\" she said.",
                [
                    Item("\"Hello.\"", "dialog", "Bob"),
                    Item("she said.", "narration", "narrator"),
                ])],
                []);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var segments = doc.RootElement.GetProperty("preceding")[0].GetProperty("segments");
            Assert.Equal(2, segments.GetArrayLength());
            Assert.Equal("\"Hello.\"", segments[0].GetProperty("text").GetString());
            Assert.Equal("dialog", segments[0].GetProperty("type").GetString());
            Assert.Equal("Bob", segments[0].GetProperty("speaker").GetString());
            Assert.Equal("narrator", segments[1].GetProperty("speaker").GetString());
        }

        [Fact]
        public void BuildContextJson_UnattributedContextSegment_KeepsUnknownSentinel()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("Who said this?", []),
                [new ContextParagraph("\"Something\"",
                    [Item("\"Something\"", "dialog", "unknown")])],
                []);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var segments = doc.RootElement.GetProperty("preceding")[0].GetProperty("segments");
            Assert.Equal("unknown", segments[0].GetProperty("speaker").GetString());
        }

        [Fact]
        public void BuildContextJson_QueryIsIndexedItems_NarrationIncluded_NoSpeakers()
        {
            var ctx = new ParagraphContext(
                // The split is frozen: the query paragraph is asked as its own items, narration
                // included so the attribution tag stays visible.
                new ContextParagraph("\"Go.\" she said.",
                [
                    Item("\"Go.\"", "dialog", "unknown"),
                    Item("she said.", "narration", "narrator"),
                ]),
                [],
                [new ContextParagraph("After.", [Item("After.", "narration", "narrator")])]);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var query = doc.RootElement.GetProperty("query");
            Assert.Equal(JsonValueKind.Object, query.ValueKind);
            Assert.False(query.TryGetProperty("text", out _));
            Assert.False(query.TryGetProperty("segments", out _));

            var items = query.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.Equal([0, 1], items.EnumerateArray().Select(i => i.GetProperty("index").GetInt32()));
            Assert.Equal("dialog", items[0].GetProperty("type").GetString());
            Assert.Equal("\"Go.\"", items[0].GetProperty("text").GetString());
            Assert.Equal("narration", items[1].GetProperty("type").GetString());
            Assert.Equal("she said.", items[1].GetProperty("text").GetString());
            // No speaker on the query: that is the thing being asked for.
            Assert.False(items[0].TryGetProperty("speaker", out _));

            var following = doc.RootElement.GetProperty("following")[0];
            Assert.Equal("narrator", following.GetProperty("segments")[0].GetProperty("speaker").GetString());
        }

        [Fact]
        public void DefaultBatchCharacterPrompt_ContainsAllDeclaredTokens()
        {
            var prompt = PromptTemplates.DefaultBatchCharacterPrompt;
            Assert.Contains("{{" + PromptTemplates.BookTitle + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.BookAuthor + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.KnownCharacters + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.ContextJson + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.ResponseFormat + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.NarratorIdentity + "}}", prompt);
        }

        [Fact]
        public void BuildBatchContextJson_TargetsGetIndexAndItems_ContextGetsSegments()
        {
            var ctx = new ParagraphBatchContext(
                [
                    new BatchContextEntry("Before.",
                        [Item("Before.", "narration", "narrator")], null),
                    new BatchContextEntry("\"First target.\" he said.",
                    [
                        Item("\"First target.\"", "dialog", "unknown"),
                        Item("he said.", "narration", "narrator"),
                    ], 0),
                    new BatchContextEntry("\"Known line.\"",
                        [Item("\"Known line.\"", "dialog", "Alice")], null),
                    new BatchContextEntry("\"Second target.\"",
                        [Item("\"Second target.\"", "dialog", "unknown")], 1),
                ],
                [Guid.NewGuid(), Guid.NewGuid()],
                []);

            var json = PromptTemplates.BuildBatchContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var paragraphs = doc.RootElement.GetProperty("paragraphs");
            Assert.Equal(4, paragraphs.GetArrayLength());

            Assert.Equal("narrator", paragraphs[0].GetProperty("segments")[0].GetProperty("speaker").GetString());
            Assert.False(paragraphs[0].TryGetProperty("index", out _));
            Assert.False(paragraphs[0].TryGetProperty("items", out _));

            // A target: paragraph "index", then its own items numbered 0..n-1, narration included.
            Assert.Equal(0, paragraphs[1].GetProperty("index").GetInt32());
            Assert.False(paragraphs[1].TryGetProperty("segments", out _));
            Assert.False(paragraphs[1].TryGetProperty("text", out _));
            var items = paragraphs[1].GetProperty("items");
            Assert.Equal([0, 1], items.EnumerateArray().Select(i => i.GetProperty("index").GetInt32()));
            Assert.Equal("\"First target.\"", items[0].GetProperty("text").GetString());
            Assert.Equal("narration", items[1].GetProperty("type").GetString());
            Assert.False(items[0].TryGetProperty("speaker", out _));

            // Item indices restart per target paragraph — they are local to the paragraph answered.
            Assert.Equal("Alice", paragraphs[2].GetProperty("segments")[0].GetProperty("speaker").GetString());
            Assert.Equal(1, paragraphs[3].GetProperty("index").GetInt32());
            Assert.Equal(0, paragraphs[3].GetProperty("items")[0].GetProperty("index").GetInt32());
        }

        [Fact]
        public void BuildContextJson_EmptyContext_ProducesEmptyArrays()
        {
            var ctx = new ParagraphContext(new ContextParagraph("Lone paragraph.", []), [], []);
            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            Assert.Equal(0, doc.RootElement.GetProperty("preceding").GetArrayLength());
            Assert.Equal(0, doc.RootElement.GetProperty("following").GetArrayLength());
        }
    }
}
