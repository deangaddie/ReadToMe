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
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("""{"choices":[]}""")]
        [InlineData("""{"choices":[{}]}""")]
        [InlineData("""{"choices":[{"delta":{}}]}""")]
        [InlineData("""{"choices":[{"delta":{"content":null}}]}""")]
        public void ParseChunk_NoUsableContent_ReturnsNull(string payload)
        {
            Assert.Null(OpenAiStreamParser.ParseChunk(payload));
        }

        [Fact]
        public void ParseChunk_TextChunk_CarriesNoMetrics()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """{"choices":[{"delta":{"content":"Hello"},"finish_reason":null}]}""");

            Assert.Equal("Hello", chunk!.Content);
            Assert.Null(chunk.Timings);
            Assert.Null(chunk.Usage);
        }

        // The real llama.cpp shape when include_usage is off: timings ride the finish_reason
        // chunk, whose delta is an empty object. A delta-first parser drops this.
        [Fact]
        public void ParseChunk_DeltaLessFinishChunkWithTimings_IsSurfaced()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """
                {"choices":[{"finish_reason":"stop","index":0,"delta":{}}],
                 "object":"chat.completion.chunk",
                 "timings":{"cache_n":236,"prompt_n":1,"prompt_ms":30.958,
                            "prompt_per_token_ms":30.958,"prompt_per_second":32.30,
                            "predicted_n":35,"predicted_ms":661.064,
                            "predicted_per_token_ms":18.887,"predicted_per_second":52.944}}
                """);

            Assert.NotNull(chunk);
            Assert.Null(chunk!.Content);
            Assert.Null(chunk.Thinking);
            Assert.Null(chunk.Usage);

            var timings = Assert.IsType<LlmTimings>(chunk.Timings);
            Assert.Equal(236, timings.CacheN);
            Assert.Equal(1, timings.PromptN);
            Assert.Equal(30.958, timings.PromptMs);
            Assert.Equal(35, timings.PredictedN);
            Assert.Equal(661.064, timings.PredictedMs);
        }

        // The real shape with include_usage on: choices is an empty ARRAY and timings move onto
        // the usage chunk. This is the second of the two ways the metrics chunk arrives delta-less.
        [Fact]
        public void ParseChunk_EmptyChoicesUsageChunk_IsSurfaced()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """
                {"choices":[],
                 "object":"chat.completion.chunk",
                 "usage":{"completion_tokens":35,"prompt_tokens":237,"total_tokens":272,
                          "prompt_tokens_details":{"cached_tokens":236}},
                 "timings":{"cache_n":236,"prompt_n":1,"prompt_ms":30.958,
                            "predicted_n":35,"predicted_ms":661.064}}
                """);

            Assert.NotNull(chunk);
            Assert.Null(chunk!.Content);

            var usage = Assert.IsType<LlmUsage>(chunk.Usage);
            Assert.Equal(237, usage.PromptTokens);
            Assert.Equal(35, usage.CompletionTokens);
            Assert.Equal(272, usage.TotalTokens);
            Assert.Equal(236, usage.CachedTokens);

            Assert.Equal(35, chunk.Timings!.PredictedN);
        }

        [Fact]
        public void ParseChunk_UsageWithoutTokenDetails_LeavesCachedNull()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """{"choices":[],"usage":{"completion_tokens":3,"prompt_tokens":7,"total_tokens":10}}""");

            Assert.Equal(7, chunk!.Usage!.PromptTokens);
            Assert.Null(chunk.Usage.CachedTokens);
        }

        // timings_per_token: true — timings attach to deltas.back(), which does carry content.
        // A chunk may carry text and metrics at once.
        [Fact]
        public void ParseChunk_ContentAndTimingsTogether_CarriesBoth()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """
                {"choices":[{"delta":{"content":"Hel"},"finish_reason":null}],
                 "timings":{"cache_n":0,"prompt_n":12,"prompt_ms":100.5,
                            "predicted_n":3,"predicted_ms":40.25}}
                """);

            Assert.Equal("Hel", chunk!.Content);
            Assert.Equal(3, chunk.Timings!.PredictedN);
            Assert.Equal(40.25, chunk.Timings.PredictedMs);
        }

        // A fully cache-hit prompt makes llama.cpp divide by zero unguarded; nlohmann serializes
        // the inf/nan as null. Null must survive as null — 0 would be a lie.
        [Fact]
        public void ParseChunk_NullTimingFields_StayNullNeverZero()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """
                {"choices":[{"finish_reason":"stop","index":0,"delta":{}}],
                 "timings":{"cache_n":236,"prompt_n":0,"prompt_ms":null,
                            "prompt_per_token_ms":null,"prompt_per_second":null,
                            "predicted_n":35,"predicted_ms":661.064}}
                """);

            var timings = chunk!.Timings!;
            Assert.Null(timings.PromptMs);
            Assert.Equal(0, timings.PromptN);
            Assert.Equal(236, timings.CacheN);
            Assert.Equal(661.064, timings.PredictedMs);
        }

        [Fact]
        public void ParseChunk_TimingsObjectWithNoKnownFields_YieldsAllNullPayload()
        {
            var chunk = OpenAiStreamParser.ParseChunk(
                """{"choices":[{"finish_reason":"stop","delta":{}}],"timings":{}}""");

            Assert.NotNull(chunk);
            Assert.Null(chunk!.Timings!.PredictedN);
            Assert.Null(chunk.Timings.PredictedMs);
        }

        [Theory]
        [InlineData("""{"choices":[{"delta":{}}],"timings":null}""")]
        [InlineData("""{"choices":[{"delta":{}}],"timings":"nope"}""")]
        [InlineData("""{"choices":[],"usage":null}""")]
        public void ParseChunk_NonObjectMetrics_ReturnsNull(string payload)
        {
            Assert.Null(OpenAiStreamParser.ParseChunk(payload));
        }

        [Fact]
        public void ParseLine_DeltaLessTimingsChunk_ReturnsChunkNotIgnored()
        {
            var result = OpenAiStreamParser.ParseLine(
                """data: {"choices":[{"finish_reason":"stop","index":0,"delta":{}}],"timings":{"predicted_n":35,"predicted_ms":661.064}}""");

            Assert.Equal(OpenAiStreamParser.LineKind.Chunk, result.Kind);
            Assert.Equal(35, result.Chunk!.Timings!.PredictedN);
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
