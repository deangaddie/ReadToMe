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
