using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class OpenAiStreamParserTests
    {
        [Theory]
        [InlineData("")]
        [InlineData(": keep-alive comment")]
        [InlineData("event: message")]
        public void ParseLine_NonDataLine_Ignored(string line)
        {
            Assert.Equal(OpenAiStreamParser.LineKind.Ignore,
                OpenAiStreamParser.ParseLine(line).Kind);
        }

        [Theory]
        [InlineData("data: [DONE]")]
        [InlineData("data:[DONE]")]
        public void ParseLine_DoneSentinel_ReturnsDone(string line)
        {
            Assert.Equal(OpenAiStreamParser.LineKind.Done,
                OpenAiStreamParser.ParseLine(line).Kind);
        }

        [Fact]
        public void ParseLine_ContentDelta_ReturnsContentChunk()
        {
            var line = """data: {"choices":[{"delta":{"content":"Hello"}}]}""";
            var result = OpenAiStreamParser.ParseLine(line);

            Assert.Equal(OpenAiStreamParser.LineKind.Chunk, result.Kind);
            Assert.Equal("Hello", result.Chunk!.Content);
            Assert.Null(result.Chunk.Thinking);
            Assert.False(result.Chunk.Done);
        }

        [Fact]
        public void ParseChunk_ReasoningContentField_MapsToThinking()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """{"choices":[{"delta":{"reasoning_content":"hmm"}}]}""");
            Assert.Equal("hmm", chunk!.Thinking);
            Assert.Null(chunk.Content);
        }

        [Fact]
        public void ParseChunk_LegacyReasoningField_MapsToThinking()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """{"choices":[{"delta":{"reasoning":"hmm"}}]}""");
            Assert.Equal("hmm", chunk!.Thinking);
        }

        [Fact]
        public void ParseChunk_PrefersReasoningContentOverReasoning()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """{"choices":[{"delta":{"reasoning_content":"a","reasoning":"b"}}]}""");
            Assert.Equal("a", chunk!.Thinking);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("""{"choices":[]}""")]
        [InlineData("""{"choices":[{}]}""")]
        [InlineData("""{"choices":[{"delta":{}}]}""")]
        [InlineData("""{"choices":[{"delta":{"content":null}}]}""")]
        public void ParseChunk_NoUsableContent_ReturnsNull(string payload)
        {
            Assert.Null(OpenAiStreamParser.ParseChunk(payload));
        }

        [Theory]
        [InlineData("http://localhost:8080", "v1/models", "http://localhost:8080/v1/models")]
        [InlineData("http://localhost:8080/", "v1/models", "http://localhost:8080/v1/models")]
        [InlineData("http://h//", "v1/x", "http://h/v1/x")]
        public void Combine_NormalisesTrailingSlash(string baseUrl, string path, string expected)
        {
            Assert.Equal(expected, OpenAiStreamParser.Combine(baseUrl, path));
        }
    }
}
