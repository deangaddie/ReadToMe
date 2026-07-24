using Read2Me.AppData.Entities;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class OpenAiRequestBuilderTests
    {
        [Fact]
        public void BuildChatBody_OmitsModelWhenBlank()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);
            Assert.DoesNotContain("\"model\"", json);
        }

        [Fact]
        public void BuildChatBody_IncludesModelWhenSet()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x", Model = "gemma-4b" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);
            Assert.Contains("\"model\"", json);
            Assert.Contains("gemma-4b", json);
        }

        [Fact]
        public void BuildChatBody_EmitsOnlySetNumericParams()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x", Temperature = 0.5 };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: false);
            Assert.Contains("\"temperature\"", json);
            Assert.DoesNotContain("top_p", json);
            Assert.DoesNotContain("max_tokens", json);
            Assert.DoesNotContain("frequency_penalty", json);
            Assert.DoesNotContain("presence_penalty", json);
        }

        [Fact]
        public void BuildChatBody_StreamBooleanAlwaysPresent()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var jsonTrue = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);
            var jsonFalse = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: false);
            Assert.Contains("\"stream\":true", jsonTrue);
            Assert.Contains("\"stream\":false", jsonFalse);
        }

        [Fact]
        public void BuildChatBody_OmitsChatTemplateKwargsByDefault()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);
            Assert.DoesNotContain("chat_template_kwargs", json);
        }

        [Fact]
        public void BuildChatBody_EmitsEnableThinkingFalseWhenThinkingDisabled()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true, disableThinking: true);
            Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false}", json);
        }

        [Fact]
        public void BuildChatBody_OmitsResponseFormatWhenNoSchema()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);
            Assert.DoesNotContain("response_format", json);
        }

        [Fact]
        public void BuildChatBody_EmitsJsonSchemaResponseFormat()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var schema = """{ "type": "object", "properties": { "character": { "type": "string" } } }""";
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true, schema);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var format = doc.RootElement.GetProperty("response_format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var inner = format.GetProperty("json_schema").GetProperty("schema");
            Assert.Equal("object", inner.GetProperty("type").GetString());
        }

        [Fact]
        public void BuildChatBody_AsksForPerTokenTimings()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("timings_per_token").GetBoolean());
        }

        [Fact]
        public void BuildChatBody_AsksForUsageOnStream()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: true);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var options = doc.RootElement.GetProperty("stream_options");
            Assert.True(options.GetProperty("include_usage").GetBoolean());
        }

        // stream_options is meaningless on a non-streamed request and the OpenAI spec rejects it
        // there; the usage totals come back in the response body anyway.
        [Fact]
        public void BuildChatBody_OmitsStreamOptionsWhenNotStreaming()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: false);
            Assert.DoesNotContain("stream_options", json);
        }

        [Fact]
        public void BuildChatBody_OverrideWinsOverConfigTemperatureAndMaxTokens()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x", Temperature = 0.5, MaxTokens = 100 };
            var json = OpenAiRequestBuilder.BuildChatBody(
                cfg, "hi", stream: false, overrides: new LlmRunOverrides(MaxTokens: 4096, Temperature: 0.1));

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(0.1, doc.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public void BuildChatBody_NullOverridePropertiesFallBackToConfig()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x", Temperature = 0.5, MaxTokens = 100 };
            var json = OpenAiRequestBuilder.BuildChatBody(
                cfg, "hi", stream: false, overrides: new LlmRunOverrides());

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(0.5, doc.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(100, doc.RootElement.GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public void BuildChatBody_NoOverridesUsesConfigValues()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x", Temperature = 0.5, MaxTokens = 100 };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hi", stream: false);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(0.5, doc.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(100, doc.RootElement.GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public void BuildChatBody_UnsetConfigMaxTokensStillOmitsMaxTokens()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(
                cfg, "hi", stream: false, overrides: new LlmRunOverrides(Temperature: 0.1));
            Assert.DoesNotContain("max_tokens", json);
        }

        [Fact]
        public void BuildChatBody_OverrideSuppliesMaxTokensWhenConfigHasNone()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(
                cfg, "hi", stream: false, overrides: new LlmRunOverrides(MaxTokens: 512));

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(512, doc.RootElement.GetProperty("max_tokens").GetInt32());
        }

        [Fact]
        public void BuildChatBody_SingleUserMessageWithPrompt()
        {
            var cfg = new LlmServerConfig { BaseUrl = "http://x" };
            var json = OpenAiRequestBuilder.BuildChatBody(cfg, "hello world", stream: false);
            Assert.Contains("\"role\":\"user\"", json);
            Assert.Contains("\"content\":\"hello world\"", json);
        }
    }
}
