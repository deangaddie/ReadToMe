using System.Collections.Generic;
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
        public void DefaultContextWindowConstants_AreCorrect()
        {
            Assert.Equal(4, PromptTemplates.DefaultContextParagraphsBefore);
            Assert.Equal(0, PromptTemplates.DefaultContextParagraphsAfter);
        }

        [Fact]
        public void BuildContextJson_KnownSpeaker_EmitsSpeakerField()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("Who said this?", null),
                [new ContextParagraph("Hello.", "Bob")],
                []);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var preceding = doc.RootElement.GetProperty("preceding")[0];
            Assert.Equal("Hello.", preceding.GetProperty("paragraph").GetString());
            Assert.Equal("Bob", preceding.GetProperty("speaker").GetString());
        }

        [Fact]
        public void BuildContextJson_UnknownSpeaker_OmitsSpeakerField()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("Who said this?", null),
                [new ContextParagraph("\"Something\"", null)],
                []);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            var preceding = doc.RootElement.GetProperty("preceding")[0];
            Assert.False(preceding.TryGetProperty("speaker", out _));
        }

        [Fact]
        public void BuildContextJson_QueryIsObject_NotArray()
        {
            var ctx = new ParagraphContext(
                new ContextParagraph("Target.", null),
                [],
                [new ContextParagraph("After.", "Narrator")]);

            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("query").ValueKind);
            Assert.Equal("Target.", doc.RootElement.GetProperty("query").GetProperty("paragraph").GetString());

            var following = doc.RootElement.GetProperty("following")[0];
            Assert.Equal("Narrator", following.GetProperty("speaker").GetString());
        }

        [Fact]
        public void BuildContextJson_EmptyContext_ProducesEmptyArrays()
        {
            var ctx = new ParagraphContext(new ContextParagraph("Lone paragraph.", null), [], []);
            var json = PromptTemplates.BuildContextJson(ctx);
            var doc = JsonDocument.Parse(json);

            Assert.Equal(0, doc.RootElement.GetProperty("preceding").GetArrayLength());
            Assert.Equal(0, doc.RootElement.GetProperty("following").GetArrayLength());
        }
    }
}
