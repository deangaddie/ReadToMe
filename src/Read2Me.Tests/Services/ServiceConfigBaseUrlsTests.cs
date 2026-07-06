using Read2Me.AppData.Entities;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ServiceConfigBaseUrlsTests
    {
        [Fact]
        public void Llm_ReturnsDirectBaseUrl()
        {
            var config = new LlmServerConfig { BaseUrl = "http://localhost:8080" };
            Assert.Equal("http://localhost:8080", ServiceConfigBaseUrls.For(config));
        }

        [Fact]
        public void Llm_BlankBaseUrl_ReturnsNull()
        {
            Assert.Null(ServiceConfigBaseUrls.For(new LlmServerConfig { BaseUrl = " " }));
        }

        [Fact]
        public void ParagraphTts_VoxCpm2_ReadsBaseUrlFromSettingsJson()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.VoxCpm2,
                // VoxCpm2ParagraphTtsSettings maps BaseUrl via [JsonPropertyName("baseUrl")].
                SettingsJson = """{"baseUrl":"http://localhost:8003"}""",
            };
            Assert.Equal("http://localhost:8003", ServiceConfigBaseUrls.For(config));
        }

        [Theory]
        [InlineData("""{"BaseUrl":"http://localhost:8000"}""")]
        [InlineData("""{"baseUrl":"http://localhost:8000"}""")]
        public void ParagraphTts_Chatterbox_ReadsBaseUrlFromSettingsJson(string settingsJson)
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.Chatterbox,
                SettingsJson = settingsJson,
            };
            Assert.Equal("http://localhost:8000", ServiceConfigBaseUrls.For(config));
        }

        [Theory]
        [InlineData("""{"BaseUrl":"http://localhost:8001"}""")]
        [InlineData("""{"baseUrl":"http://localhost:8001"}""")]
        public void ParagraphTts_ChatterboxTurbo_ReadsBaseUrlFromSettingsJson(string settingsJson)
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.ChatterboxTurbo,
                SettingsJson = settingsJson,
            };
            Assert.Equal("http://localhost:8001", ServiceConfigBaseUrls.For(config));
        }

        [Theory]
        [InlineData("""{"BaseUrl":"http://localhost:8101"}""")]
        [InlineData("""{"baseUrl":"http://localhost:8101"}""")]
        public void ParagraphTts_Qwen3Base_ReadsBaseUrlFromSettingsJson(string settingsJson)
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.Qwen3Base,
                SettingsJson = settingsJson,
            };
            Assert.Equal("http://localhost:8101", ServiceConfigBaseUrls.For(config));
        }

        [Fact]
        public void Transcription_LocalWhisper_ReadsBaseUrlFromSettingsJson()
        {
            var config = new TranscriptionServiceConfig
            {
                Type = TranscriptionServiceType.LocalWhisper,
                SettingsJson = """{"BaseUrl":"http://localhost:9000"}""",
            };
            Assert.Equal("http://localhost:9000", ServiceConfigBaseUrls.For(config));
        }

        [Theory]
        [InlineData(VoiceDesignServiceType.VoxCpm2, "http://localhost:8003")]
        [InlineData(VoiceDesignServiceType.Qwen3, "http://localhost:8100")]
        public void VoiceDesign_ReadsBaseUrlPerType(VoiceDesignServiceType type, string url)
        {
            var config = new VoiceDesignServiceConfig
            {
                Type = type,
                SettingsJson = $$"""{"BaseUrl":"{{url}}"}""",
            };
            Assert.Equal(url, ServiceConfigBaseUrls.For(config));
        }

        [Theory]
        [InlineData(VoiceDesignServiceType.VoxCpm2)]
        [InlineData(VoiceDesignServiceType.Qwen3)]
        public void VoiceDesign_CamelCaseSettingsJson_StillResolves(VoiceDesignServiceType type)
        {
            // The VoxCpm2 voice-design form serializes SettingsJson with Web options — "baseUrl",
            // not "BaseUrl". A case-sensitive parse here made pre-flight treat the endpoint as
            // unmanaged, so the GPU-swap dialog (stop llama → start voxcpm2) never appeared.
            var config = new VoiceDesignServiceConfig
            {
                Type = type,
                SettingsJson = """{"baseUrl":"http://localhost:8003"}""",
            };
            Assert.Equal("http://localhost:8003", ServiceConfigBaseUrls.For(config));
        }

        [Fact]
        public void SemanticSimilarity_ReadsBaseUrlFromSettingsJson()
        {
            var config = new SemanticSimilarityServiceConfig
            {
                SettingsJson = """{"BaseUrl":"http://localhost:8200"}""",
            };
            Assert.Equal("http://localhost:8200", ServiceConfigBaseUrls.For(config));
        }

        [Fact]
        public void MalformedJson_ReturnsNull()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{not json",
            };
            Assert.Null(ServiceConfigBaseUrls.For(config));
        }

        [Fact]
        public void EmptyJsonObject_ReturnsNull()
        {
            var config = new TranscriptionServiceConfig
            {
                Type = TranscriptionServiceType.LocalWhisper,
                SettingsJson = "{}",
            };
            Assert.Null(ServiceConfigBaseUrls.For(config));
        }
    }
}
