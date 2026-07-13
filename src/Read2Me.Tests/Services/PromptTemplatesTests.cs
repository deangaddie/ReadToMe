using System.Text.Json;
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
        }

        [Fact]
        public void DefaultVoicePrompt_ContainsCharacterNameToken()
        {
            var prompt = PromptTemplates.DefaultVoicePrompt;
            Assert.Contains("{{" + PromptTemplates.CharacterName + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.BookTitle + "}}", prompt);
            Assert.Contains("{{" + PromptTemplates.BookAuthor + "}}", prompt);
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

        [Fact]
        public void BuildContextJson_ContextParagraphs_EmitSegmentsWithSpeakers()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("Who said this?", []),
                [new ContextParagraph("\"Hello.\" she said.",
                [
                    new ContextSegment("\"Hello.\"", "dialog", "Bob"),
                    new ContextSegment("she said.", "narration", "narrator"),
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
                    [new ContextSegment("\"Something\"", "dialog", "unknown")])],
                []);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var segments = doc.RootElement.GetProperty("preceding")[0].GetProperty("segments");
            Assert.Equal("unknown", segments[0].GetProperty("speaker").GetString());
        }

        [Fact]
        public void BuildContextJson_QueryIsRawTextOnly_NoSegments()
        {
            var ctx = new ParagraphContext(
                // The query's current split is never fed back — it may be exactly what is wrong.
                new ContextParagraph("Target.", [new ContextSegment("Target.", "dialog", "unknown")]),
                [],
                [new ContextParagraph("After.", [new ContextSegment("After.", "narration", "narrator")])]);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var query = doc.RootElement.GetProperty("query");
            Assert.Equal(JsonValueKind.Object, query.ValueKind);
            Assert.Equal("Target.", query.GetProperty("text").GetString());
            Assert.False(query.TryGetProperty("segments", out _));

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
        }

        [Fact]
        public void BuildBatchContextJson_TargetsGetIndexAndRawText_ContextGetsSegments()
        {
            var ctx = new ParagraphBatchContext(
                [
                    new BatchContextEntry("Before.",
                        [new ContextSegment("Before.", "narration", "narrator")], null),
                    new BatchContextEntry("\"First target.\"",
                        [new ContextSegment("\"First target.\"", "dialog", "unknown")], 0),
                    new BatchContextEntry("\"Known line.\"",
                        [new ContextSegment("\"Known line.\"", "dialog", "Alice")], null),
                    new BatchContextEntry("\"Second target.\"", [], 1),
                ],
                [Guid.NewGuid(), Guid.NewGuid()],
                []);

            var json = PromptTemplates.BuildBatchContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var paragraphs = doc.RootElement.GetProperty("paragraphs");
            Assert.Equal(4, paragraphs.GetArrayLength());

            Assert.Equal("narrator", paragraphs[0].GetProperty("segments")[0].GetProperty("speaker").GetString());
            Assert.False(paragraphs[0].TryGetProperty("index", out _));
            Assert.False(paragraphs[0].TryGetProperty("text", out _));

            Assert.Equal(0, paragraphs[1].GetProperty("index").GetInt32());
            Assert.Equal("\"First target.\"", paragraphs[1].GetProperty("text").GetString());
            Assert.False(paragraphs[1].TryGetProperty("segments", out _));

            Assert.Equal("Alice", paragraphs[2].GetProperty("segments")[0].GetProperty("speaker").GetString());
            Assert.Equal(1, paragraphs[3].GetProperty("index").GetInt32());
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
