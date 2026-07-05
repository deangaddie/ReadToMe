using NSubstitute;
using Read2Me.App.Services.Preflight;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.App.Preflight
{
    public class AiTaskRequirementsResolverTests
    {
        private readonly LlmSettingsService _llm = Substitute.For<LlmSettingsService>(null!, null!);
        private readonly ParagraphTtsSettingsService _tts = Substitute.For<ParagraphTtsSettingsService>(null!, null!);
        private readonly TranscriptionSettingsService _transcription = Substitute.For<TranscriptionSettingsService>(null!, null!);
        private readonly SemanticSimilaritySettingsService _similarity = Substitute.For<SemanticSimilaritySettingsService>(null!, null!);
        private readonly VoiceDesignSettingsService _voiceDesign = Substitute.For<VoiceDesignSettingsService>(null!, null!);

        private AiTaskRequirementsResolver CreateResolver() =>
            new(_llm, _tts, _transcription, _similarity, _voiceDesign);

        private void SetActive(
            string? llmUrl = null, string? ttsUrl = null, string? transcriptionUrl = null,
            string? similarityUrl = null, string? voiceDesignUrl = null)
        {
            _llm.GetActiveConfigAsync().Returns(
                llmUrl is null ? null : new LlmServerConfig { BaseUrl = llmUrl });
            _tts.GetActiveConfigAsync().Returns(
                ttsUrl is null ? null : new ParagraphTtsServiceConfig
                {
                    Type = ParagraphTtsServiceType.VoxCpm2,
                    // VoxCpm2ParagraphTtsSettings maps BaseUrl via [JsonPropertyName("baseUrl")].
                    SettingsJson = $$"""{"baseUrl":"{{ttsUrl}}"}""",
                });
            _transcription.GetActiveConfigAsync().Returns(
                transcriptionUrl is null ? null : new TranscriptionServiceConfig
                {
                    Type = TranscriptionServiceType.LocalWhisper,
                    SettingsJson = $$"""{"BaseUrl":"{{transcriptionUrl}}"}""",
                });
            _similarity.GetActiveConfigAsync().Returns(
                similarityUrl is null ? null : new SemanticSimilarityServiceConfig
                {
                    SettingsJson = $$"""{"BaseUrl":"{{similarityUrl}}"}""",
                });
            _voiceDesign.GetActiveConfigAsync().Returns(
                voiceDesignUrl is null ? null : new VoiceDesignServiceConfig
                {
                    Type = VoiceDesignServiceType.VoxCpm2,
                    SettingsJson = $$"""{"BaseUrl":"{{voiceDesignUrl}}"}""",
                });
        }

        [Theory]
        [InlineData(AiTaskKind.CharacterAttribution)]
        [InlineData(AiTaskKind.VoicePromptGeneration)]
        public async Task LlmTasks_ReturnActiveLlmUrl(AiTaskKind kind)
        {
            SetActive(llmUrl: "http://localhost:8080");

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(kind, CancellationToken.None);

            Assert.Equal(["http://localhost:8080"], urls);
        }

        [Fact]
        public async Task AudioGeneration_ReturnsTtsTranscriptionAndSimilarityUrls()
        {
            SetActive(
                ttsUrl: "http://localhost:8003",
                transcriptionUrl: "http://localhost:9000",
                similarityUrl: "http://localhost:8200");

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(AiTaskKind.AudioGeneration, CancellationToken.None);

            Assert.Equal(["http://localhost:8003", "http://localhost:9000", "http://localhost:8200"], urls);
        }

        [Fact]
        public async Task AudioGeneration_MissingActiveConfigs_ContributeNothing()
        {
            SetActive(ttsUrl: "http://localhost:8003");

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(AiTaskKind.AudioGeneration, CancellationToken.None);

            Assert.Equal(["http://localhost:8003"], urls);
        }

        [Fact]
        public async Task VoiceDesignAudio_ReturnsActiveVoiceDesignUrl()
        {
            SetActive(voiceDesignUrl: "http://localhost:8003");

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(AiTaskKind.VoiceDesignAudio, CancellationToken.None);

            Assert.Equal(["http://localhost:8003"], urls);
        }

        [Fact]
        public async Task Transcription_ReturnsActiveTranscriptionUrl()
        {
            SetActive(transcriptionUrl: "http://localhost:9000");

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(AiTaskKind.Transcription, CancellationToken.None);

            Assert.Equal(["http://localhost:9000"], urls);
        }

        [Fact]
        public async Task NoActiveConfig_ReturnsEmpty()
        {
            SetActive();

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(AiTaskKind.CharacterAttribution, CancellationToken.None);

            Assert.Empty(urls);
        }

        [Fact]
        public async Task DuplicateUrls_AreDeduplicated()
        {
            SetActive(
                ttsUrl: "http://localhost:8003",
                transcriptionUrl: "http://localhost:8003",
                similarityUrl: "http://localhost:8003");

            var urls = await CreateResolver().GetRequiredBaseUrlsAsync(AiTaskKind.AudioGeneration, CancellationToken.None);

            Assert.Equal(["http://localhost:8003"], urls);
        }
    }
}
