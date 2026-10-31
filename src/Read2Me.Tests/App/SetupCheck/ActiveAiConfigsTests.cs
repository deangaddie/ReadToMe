using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The one read of the active AI configs over the test database: each config paired with the
    /// managed service its URL resolves to, through the service-control fake.
    /// </summary>
    public class ActiveAiConfigsTests : AppDbTestBase
    {
        private const string Llama = "http://localhost:8080";
        private const string AudioCpp = "http://localhost:8004";
        private const string Whisper = "http://localhost:9000";
        private const string MiniLm = "http://localhost:8200";

        private readonly FakeAiServiceControl _services = new();

        public ActiveAiConfigsTests()
        {
            foreach (var service in new DockerAiServiceRegistry().All)
                _services.ResolveByUrl[service.BaseUrl] = service;
        }

        private Task<ActiveAiConfigs> ReadAsync() => ActiveAiConfigs.ReadAsync(
            SetupCheckTestServices.Create(Factory, new ConfigurationBuilder().Build(), _services));

        private LlmSettingsService Llm() => new(Factory, NullLogger<LlmSettingsService>.Instance);

        private Task<LlmServerConfig> AddLlmAsync(string name, string baseUrl, string model) =>
            Llm().CreateConfigAsync(new LlmServerConfig { Name = name, BaseUrl = baseUrl, Model = model });

        private Task AddWhisperAsync() =>
            new TranscriptionSettingsService(Factory, NullLogger<TranscriptionSettingsService>.Instance).CreateConfigAsync(
                new TranscriptionServiceConfig
                {
                    Name = "Whisper",
                    Type = TranscriptionServiceType.LocalWhisper,
                    SettingsJson = $$"""{"BaseUrl":"{{Whisper}}"}""",
                });

        private async Task AddEveryConfigAsync(string llmUrl = Llama)
        {
            await AddLlmAsync("Qwen", llmUrl, "qwen-28b");
            await new ParagraphTtsSettingsService(Factory, NullLogger<ParagraphTtsSettingsService>.Instance).CreateConfigAsync(
                new ParagraphTtsServiceConfig
                {
                    Name = "Breeze",
                    Type = ParagraphTtsServiceType.Breeze,
                    SettingsJson = $$"""{"baseUrl":"{{AudioCpp}}","modelId":"breeze-q8"}""",
                });
            await new VoiceDesignSettingsService(Factory, NullLogger<VoiceDesignSettingsService>.Instance).CreateConfigAsync(
                new VoiceDesignServiceConfig
                {
                    Name = "Breeze design",
                    Type = VoiceDesignServiceType.Breeze,
                    SettingsJson = $$"""{"baseUrl":"{{AudioCpp}}","modelId":"breeze-design"}""",
                });
            await AddWhisperAsync();
            await new SemanticSimilaritySettingsService(Factory, NullLogger<SemanticSimilaritySettingsService>.Instance).CreateConfigAsync(
                new SemanticSimilarityServiceConfig
                {
                    Name = "MiniLM",
                    Type = SemanticSimilarityServiceType.MiniLmL6,
                    SettingsJson = $$"""{"BaseUrl":"{{MiniLm}}"}""",
                });
        }

        [Fact]
        public async Task With_no_configs_nothing_is_active_and_no_service_is_used()
        {
            var configs = await ReadAsync();

            Assert.Null(configs.Llm);
            Assert.Empty(configs.Chain);
            Assert.Null(configs.ParagraphTts);
            Assert.Null(configs.VoiceDesign);
            Assert.Null(configs.Transcription);
            Assert.Null(configs.Similarity);
            Assert.Empty(configs.UsedServices);
        }

        [Fact]
        public async Task Each_active_config_is_paired_with_the_managed_service_its_url_resolves_to()
        {
            await AddEveryConfigAsync();

            var configs = await ReadAsync();

            Assert.Equal(("Qwen", "llama"), (configs.Llm!.Config.Name, configs.Llm.Service?.Name));
            Assert.Equal(("Breeze", "audiocpp"), (configs.ParagraphTts!.Config.Name, configs.ParagraphTts.Service?.Name));
            Assert.Equal(("Breeze design", "audiocpp"), (configs.VoiceDesign!.Config.Name, configs.VoiceDesign.Service?.Name));
            Assert.Equal(("Whisper", "whisper"), (configs.Transcription!.Config.Name, configs.Transcription.Service?.Name));
            Assert.Equal(("MiniLM", "minilm-l6"), (configs.Similarity!.Config.Name, configs.Similarity.Service?.Name));
        }

        [Fact]
        public async Task A_config_on_a_url_the_app_does_not_manage_has_no_service()
        {
            await AddEveryConfigAsync(llmUrl: "http://gpu-box:8080");

            var configs = await ReadAsync();

            Assert.Equal("Qwen", configs.Llm!.Config.Name);
            Assert.Null(configs.Llm.Service);
            Assert.DoesNotContain(configs.UsedServices, s => s.Name == "llama");
        }

        [Fact]
        public async Task The_chain_keeps_its_order_each_entry_with_its_service()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var remote = await AddLlmAsync("Remote", "http://gpu-box:8080", "big");
            var gemma = await AddLlmAsync("Gemma", Llama, "gemma-12b");
            await Llm().SetAttributionChainEntriesAsync(
                [new AttributionChainEntry(gemma.Id, Thinking: false), new AttributionChainEntry(remote.Id, Thinking: true)]);

            var configs = await ReadAsync();

            Assert.Equal(
                [("Gemma", "llama"), ("Remote", null)],
                configs.Chain.Select(c => (c.Config.Name, c.Service?.Name)));
        }

        [Fact]
        public async Task Used_services_are_the_distinct_managed_services_the_configs_use()
        {
            await AddEveryConfigAsync();

            var configs = await ReadAsync();

            // Paragraph TTS and voice design share audiocpp; the active LLM is the default chain too.
            Assert.Equal(
                ["audiocpp", "llama", "minilm-l6", "whisper"],
                configs.UsedServices.Select(s => s.Name).Order(StringComparer.Ordinal));
        }

        [Fact]
        public async Task The_status_read_covers_the_used_services_and_every_gpu_container_when_one_is_gpu()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            await AddWhisperAsync();
            _services.StatusByName["audiocpp"] = AiServiceStatus.Ready;
            var configs = await ReadAsync();

            var statuses = await configs.ReadStatusesAsync(_services, new DockerAiServiceRegistry(), CancellationToken.None);

            // llama is GPU, so audiocpp, which its sweep would stop, is read too; unused CPU containers aren't.
            Assert.Equal(["audiocpp", "llama", "whisper"], statuses.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(AiServiceStatus.Ready, statuses["audiocpp"]);
            Assert.Equal(AiServiceStatus.Stopped, statuses["llama"]);
            Assert.Equal(3, _services.StatusCalls);
        }

        [Fact]
        public async Task With_no_gpu_service_used_the_status_read_covers_only_the_used_services()
        {
            await AddWhisperAsync();
            var configs = await ReadAsync();

            var statuses = await configs.ReadStatusesAsync(_services, new DockerAiServiceRegistry(), CancellationToken.None);

            Assert.Equal(["whisper"], statuses.Keys);
        }
    }
}
