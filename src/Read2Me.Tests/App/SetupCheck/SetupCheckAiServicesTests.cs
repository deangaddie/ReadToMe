using System.ComponentModel;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The Quick check's AI configs and Docker services rows, through <see cref="ISetupCheckRunner"/>:
    /// the configs come from the real settings services, the containers from the service-control
    /// fake, and every HTTP probe from <see cref="FakeEndpoints"/>.
    /// </summary>
    public class SetupCheckAiServicesTests : AppDbTestBase, IDisposable
    {
        private const string Llama = "http://localhost:8080";
        private const string AudioCpp = "http://localhost:8004";
        private const string Whisper = "http://localhost:9000";
        private const string MiniLm = "http://localhost:8200";

        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "r2me-setup-check", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _config = new();
        private readonly FakeAiServiceControl _services = new();
        private readonly FakeEndpoints _http = new();
        private readonly AiWatchdogOptions _watchdog = new();
        private readonly NoDocker _processes = new();

        public SetupCheckAiServicesTests()
        {
            Directory.CreateDirectory(_workspace);
            _config["Workspace:FolderPath"] = _workspace;
            // The fake manages exactly the registry's URLs, as the real control does.
            foreach (var service in new DockerAiServiceRegistry().All)
                _services.ResolveByUrl[service.BaseUrl] = service;
        }

        public void Dispose()
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }

        // ── Fakes ────────────────────────────────────────────────────────────

        /// <summary>docker answers its version unless taken off PATH; nothing else is run.</summary>
        private sealed class NoDocker : IProcessRunner
        {
            public bool OnPath = true;

            public Task<(int ExitCode, string Output)> RunAsync(
                string fileName, string arguments, TimeSpan timeout, CancellationToken ct) =>
                OnPath ? Task.FromResult((0, "Docker version 28.4.0")) : throw new Win32Exception(2, "not found");
        }

        /// <summary>ffmpeg isn't under test here: it always answers.</summary>
        private sealed class AnsweringFfmpeg : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffmpeg version 7.1"));

            public Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffprobe version 7.1"));
        }

        /// <summary>
        /// Answers GETs by absolute URL; a URL with no answer refuses the connection, as a port nothing
        /// listens on does. Records every URL asked for.
        /// </summary>
        private sealed class FakeEndpoints : HttpMessageHandler, IHttpClientFactory
        {
            public Dictionary<string, (HttpStatusCode Status, string Body)> Answers { get; } = new();
            public List<string> Requests { get; } = [];

            public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString().TrimEnd('/');
                Requests.Add($"{request.Method} {url}");
                if (!Answers.TryGetValue(url, out var answer))
                    throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
                return Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body) });
            }

            /// <summary>An OpenAI-style <c>/v1/models</c> listing of these ids.</summary>
            public void Models(string baseUrl, params string[] ids) =>
                Answers[$"{baseUrl}/v1/models"] = (HttpStatusCode.OK,
                    $$"""{"object":"list","data":[{{string.Join(",", ids.Select(id => $$"""{"id":"{{id}}"}"""))}}]}""");
        }

        // ── Builders ─────────────────────────────────────────────────────────

        private SetupCheckRunner Runner()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(_config).Build();
            return new(
                configuration,
                @"C:\app",
                Factory,
                new AnsweringFfmpeg(),
                _processes,
                new WorkspaceStart(configuration),
                SetupCheckTestServices.Create(Factory, _services, _http, _watchdog),
                SetupCheckTestServices.Full());
        }

        private async Task<SetupCheckReport> CheckAsync() => await Runner().RunQuickCheckAsync(CancellationToken.None);

        private async Task<SetupCheckRow> RowAsync(string id) => (await CheckAsync()).Rows.Single(r => r.Id == id);

        private static string Container(string service) => SetupCheckIds.Container(service);

        private async Task<LlmServerConfig> AddLlmAsync(string name, string baseUrl, string? model)
        {
            var svc = new LlmSettingsService(Factory, NullLogger<LlmSettingsService>.Instance);
            var config = await svc.CreateConfigAsync(new LlmServerConfig { Name = name, BaseUrl = baseUrl, Model = model });
            return config;
        }

        private async Task SetChainAsync(params LlmServerConfig[] configs) =>
            await new LlmSettingsService(Factory, NullLogger<LlmSettingsService>.Instance)
                .SetAttributionChainEntriesAsync([.. configs.Select(c => new AttributionChainEntry(c.Id, Thinking: false))]);

        private Task AddTtsAsync(string baseUrl, string modelId = "breeze-q8") =>
            new ParagraphTtsSettingsService(Factory, NullLogger<ParagraphTtsSettingsService>.Instance).CreateConfigAsync(
                new ParagraphTtsServiceConfig
                {
                    Name = "Breeze",
                    Type = ParagraphTtsServiceType.Breeze,
                    SettingsJson = $$"""{"baseUrl":"{{baseUrl}}","modelId":"{{modelId}}"}""",
                });

        private Task AddVoiceDesignAsync(string baseUrl, string modelId = "breeze-design") =>
            new VoiceDesignSettingsService(Factory, NullLogger<VoiceDesignSettingsService>.Instance).CreateConfigAsync(
                new VoiceDesignServiceConfig
                {
                    Name = "Breeze design",
                    Type = VoiceDesignServiceType.Breeze,
                    SettingsJson = $$"""{"baseUrl":"{{baseUrl}}","modelId":"{{modelId}}"}""",
                });

        private Task AddTranscriptionAsync(string baseUrl) =>
            new TranscriptionSettingsService(Factory, NullLogger<TranscriptionSettingsService>.Instance).CreateConfigAsync(
                new TranscriptionServiceConfig
                {
                    Name = "Whisper",
                    Type = TranscriptionServiceType.LocalWhisper,
                    SettingsJson = $$"""{"BaseUrl":"{{baseUrl}}"}""",
                });

        private Task AddSimilarityAsync(string baseUrl) =>
            new SemanticSimilaritySettingsService(Factory, NullLogger<SemanticSimilaritySettingsService>.Instance).CreateConfigAsync(
                new SemanticSimilarityServiceConfig
                {
                    Name = "MiniLM",
                    Type = SemanticSimilarityServiceType.MiniLmL6,
                    SettingsJson = $$"""{"BaseUrl":"{{baseUrl}}"}""",
                });

        /// <summary>Every active config, all on the local containers.</summary>
        private async Task AddEveryConfigAsync()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            await AddTtsAsync(AudioCpp);
            await AddVoiceDesignAsync(AudioCpp);
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
        }

        // ── Table ────────────────────────────────────────────────────────────

        [Fact]
        public async Task The_AI_configs_and_docker_services_follow_the_tools_in_table_order()
        {
            var report = await CheckAsync();

            Assert.Equal(
                [
                    SetupCheckIds.Workspace, SetupCheckIds.Ffmpeg, SetupCheckIds.Docker,
                    SetupCheckIds.Llm, SetupCheckIds.AttributionChain, SetupCheckIds.ParagraphTts,
                    SetupCheckIds.VoiceDesign, SetupCheckIds.Transcription, SetupCheckIds.Similarity,
                    Container("llama"), Container("audiocpp"), Container("whisper"),
                    Container("minilm-l6"), Container("mpnet-base-v2"),
                ],
                report.Rows.Select(r => r.Id));
            Assert.All(report.Rows.Skip(3).Take(6), r => Assert.Equal(SetupCheckGroup.AiConfigs, r.Group));
            Assert.All(report.Rows.Skip(9), r => Assert.Equal(SetupCheckGroup.DockerServices, r.Group));
            // The Full check encodes with ffmpeg and sends each AI config a real request: those rows are Full-tier.
            Assert.All(report.Rows, r => Assert.Equal(r.Id is SetupCheckIds.Ffmpeg || r.Group == SetupCheckGroup.AiConfigs ? SetupCheckTier.Full : SetupCheckTier.Quick, r.Tier));
        }

        // ── AI configs ───────────────────────────────────────────────────────

        [Theory]
        [InlineData(SetupCheckIds.Llm, "settings/llm", "Settings → LLM")]
        [InlineData(SetupCheckIds.ParagraphTts, "settings/tts", "Settings → Paragraph TTS")]
        [InlineData(SetupCheckIds.VoiceDesign, "settings/voice-design", "Settings → Voice design")]
        public async Task A_step_with_no_active_config_fails_pointing_at_its_settings(string id, string path, string label)
        {
            var row = await RowAsync(id);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("No active config", row.Found);
            Assert.Equal($"no active config — choose one in {label}", row.Fix.Text);
            Assert.Equal(new SetupSettingsLink(path, label), row.Fix.Link);
        }

        [Theory]
        [InlineData(SetupCheckIds.Transcription)]
        [InlineData(SetupCheckIds.Similarity)]
        public async Task No_transcription_or_similarity_config_is_not_used_with_the_accuracy_note(string id)
        {
            var row = await RowAsync(id);

            Assert.Equal(SetupCheckResult.NotUsed, row.Result);
            Assert.Equal("audio is generated without an accuracy score", row.Note);
        }

        [Fact]
        public async Task Active_configs_on_the_local_containers_pass_naming_what_they_use()
        {
            await AddEveryConfigAsync();

            var report = await CheckAsync();

            var llm = report.Rows.Single(r => r.Id == SetupCheckIds.Llm);
            Assert.Equal(SetupCheckResult.Passed, llm.Result);
            Assert.Equal($"Qwen · {Llama} · qwen-28b", llm.Found);
            Assert.Null(llm.Note);
            Assert.Equal($"Breeze · {AudioCpp} · breeze-q8",
                report.Rows.Single(r => r.Id == SetupCheckIds.ParagraphTts).Found);
            Assert.All(
                report.Rows.Where(r => r.Group == SetupCheckGroup.AiConfigs),
                r => Assert.Equal(SetupCheckResult.Passed, r.Result));
        }

        [Fact]
        public async Task An_active_config_with_an_unparseable_base_url_fails()
        {
            await AddLlmAsync("Broken", "localhost 8080", "qwen-28b");

            var row = await RowAsync(SetupCheckIds.Llm);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Broken: \"localhost 8080\" isn't a URL", row.Found);
            Assert.Equal("settings/llm", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task The_attribution_chain_fails_when_one_of_its_configs_has_a_bad_url()
        {
            var good = await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var bad = await AddLlmAsync("Gemma", "", "gemma-12b");
            await SetChainAsync(good, bad);

            var row = await RowAsync(SetupCheckIds.AttributionChain);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Gemma: no base URL", row.Found);
            Assert.Equal("settings/llm", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task The_attribution_chain_passes_listing_its_configs()
        {
            var qwen = await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var gemma = await AddLlmAsync("Gemma", Llama, "gemma-12b");
            await SetChainAsync(gemma, qwen);

            var row = await RowAsync(SetupCheckIds.AttributionChain);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("Gemma (gemma-12b) → Qwen (qwen-28b)", row.Found);
        }

        [Fact]
        public async Task An_unmanaged_llm_url_that_lists_models_passes_labelled_not_managed()
        {
            await AddLlmAsync("Remote", "http://gpu-box:8080", "qwen-28b");
            _http.Models("http://gpu-box:8080", "qwen-28b");

            var row = await RowAsync(SetupCheckIds.Llm);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("not managed — the app can't start or restart it", row.Note);
        }

        [Fact]
        public async Task An_unreachable_unmanaged_url_fails_pointing_at_the_config_url_in_settings()
        {
            await AddLlmAsync("Remote", "http://gpu-box:8080", "qwen-28b");

            var row = await RowAsync(SetupCheckIds.Llm);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.StartsWith("Remote: GET http://gpu-box:8080/v1/models failed", row.Found);
            Assert.Equal("not managed — the app can't start or restart it", row.Note);
            Assert.Contains("http://gpu-box:8080", row.Fix.Text);
            Assert.Equal("settings/llm", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task An_unmanaged_similarity_url_passes_on_any_http_answer()
        {
            await AddSimilarityAsync("http://embed-box:8200");
            _http.Answers["http://embed-box:8200"] = (HttpStatusCode.NotFound, "");

            var row = await RowAsync(SetupCheckIds.Similarity);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("not managed — the app can't start or restart it", row.Note);
        }

        [Fact]
        public async Task An_unmanaged_tts_url_must_list_models_to_pass()
        {
            await AddTtsAsync("http://tts-box:8004");
            _http.Answers["http://tts-box:8004/v1/models"] = (HttpStatusCode.NotFound, "");

            var row = await RowAsync(SetupCheckIds.ParagraphTts);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Breeze: GET http://tts-box:8004/v1/models failed: 404 Not Found", row.Found);
            Assert.Equal("settings/tts", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task A_chain_mixing_managed_and_unmanaged_configs_names_the_unmanaged_ones()
        {
            var local = await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var remote = await AddLlmAsync("Remote", "http://gpu-box:8080", "qwen-28b");
            await SetChainAsync(local, remote);
            _http.Models("http://gpu-box:8080", "qwen-28b");

            var row = await RowAsync(SetupCheckIds.AttributionChain);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("Remote: not managed — the app can't start or restart it", row.Note);
        }

        [Fact]
        public async Task A_managed_url_is_never_probed_from_the_config_row()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");

            await RowAsync(SetupCheckIds.Llm);

            Assert.Empty(_http.Requests);
        }

        // ── Docker services ──────────────────────────────────────────────────

        [Fact]
        public async Task Without_the_docker_cli_every_container_row_is_not_run()
        {
            await AddEveryConfigAsync();
            _processes.OnPath = false;

            var report = await CheckAsync();

            Assert.All(
                report.Rows.Where(r => r.Group == SetupCheckGroup.DockerServices),
                r =>
                {
                    Assert.Equal(SetupCheckResult.NotRun, r.Result);
                    Assert.Equal("Needs the docker CLI", r.Found);
                });
            Assert.Equal(0, _services.StatusCalls);
        }

        [Fact]
        public async Task A_container_no_active_config_uses_is_not_used_and_left_alone()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusResult = AiServiceStatus.Stopped;

            var report = await CheckAsync();

            Assert.Equal(SetupCheckResult.NotUsed, report.Rows.Single(r => r.Id == Container("whisper")).Result);
            Assert.DoesNotContain("whisper", _services.StatusAsked);
        }

        [Fact]
        public async Task A_container_used_only_by_the_attribution_chain_is_checked()
        {
            var remote = await AddLlmAsync("Remote", "http://gpu-box:8080", "qwen-28b");
            var local = await AddLlmAsync("Gemma", Llama, "gemma-12b");
            await SetChainAsync(local);
            _services.StatusByName["llama"] = AiServiceStatus.Stopped;

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.NotConfirmed, row.Result);
        }

        [Fact]
        public async Task A_stopped_container_is_not_confirmed_until_the_full_check()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusByName["llama"] = AiServiceStatus.Stopped;

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.NotConfirmed, row.Result);
            Assert.Equal("Stopped", row.Found);
            Assert.Contains("run the Full check", row.Fix.Text);
        }

        [Fact]
        public async Task A_missing_container_fails_with_the_compose_command()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusByName["llama"] = AiServiceStatus.NotFound;

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("No read2me-llama container", row.Found);
            Assert.Equal("docker compose up -d llama", row.Fix.Copy);
            Assert.Contains("Infra/", row.Fix.Text);
        }

        [Fact]
        public async Task A_down_container_fails_with_the_logs_then_compose_commands()
        {
            await AddTtsAsync(AudioCpp);
            _services.StatusByName["audiocpp"] = AiServiceStatus.Down;

            var row = await RowAsync(Container("audiocpp"));

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("docker logs -f read2me-audiocpp", row.Fix.Copy);
            Assert.Contains("docker compose up -d audiocpp", row.Fix.Text);
        }

        [Fact]
        public async Task A_running_container_not_answering_at_the_expected_url_fails_naming_it_and_the_port_mapping()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.Starting;

            var row = await RowAsync(Container("whisper"));

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal($"Running, but {Whisper}/health didn't answer", row.Found);
            Assert.Contains(Whisper, row.Fix.Text);
            Assert.Contains("port mapping", row.Fix.Text);
            Assert.Equal("Infra/docker-compose.yml", row.Fix.Copy);
        }

        [Fact]
        public async Task A_running_container_answering_an_error_is_not_confirmed_as_still_loading()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusByName["llama"] = AiServiceStatus.Starting;
            _http.Answers[$"{Llama}/health"] = (HttpStatusCode.ServiceUnavailable, """{"error":"Loading model"}""");

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.NotConfirmed, row.Result);
            Assert.Equal($"{Llama}/health answers 503: not ready yet (a model may still be loading)", row.Found);
        }

        [Fact]
        public async Task Container_rows_and_configs_on_managed_urls_are_managed_the_rest_are_not()
        {
            var local = await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var remote = await AddLlmAsync("Remote", "http://gpu-box:8080", "qwen-28b");
            await SetChainAsync(local, remote);
            await AddTtsAsync("http://tts-box:8004");
            await AddVoiceDesignAsync(AudioCpp);
            _http.Models("http://gpu-box:8080", "qwen-28b");
            _http.Models("http://tts-box:8004", "breeze-q8");

            var report = await CheckAsync();

            bool Managed(string id) => report.Rows.Single(r => r.Id == id).Managed;
            Assert.True(Managed(SetupCheckIds.Llm));
            Assert.False(Managed(SetupCheckIds.AttributionChain));
            Assert.False(Managed(SetupCheckIds.ParagraphTts));
            Assert.True(Managed(SetupCheckIds.VoiceDesign));
            Assert.False(Managed(SetupCheckIds.Transcription));
            Assert.False(Managed(SetupCheckIds.Docker));
            Assert.All(report.Rows.Where(r => r.Group == SetupCheckGroup.DockerServices), r => Assert.True(r.Managed));
        }

        [Fact]
        public async Task A_container_the_watchdog_is_restarting_is_not_confirmed()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusByName["llama"] = AiServiceStatus.Recovering;

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.NotConfirmed, row.Result);
            Assert.Equal("The watchdog is restarting it", row.Found);
        }

        [Fact]
        public async Task A_container_whose_state_docker_cannot_tell_is_not_run()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusByName["llama"] = AiServiceStatus.Unknown;

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.NotRun, row.Result);
            Assert.Contains("Docker Desktop is running", row.Fix.Text);
        }

        [Fact]
        public async Task A_ready_container_with_no_model_list_to_check_passes()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.Ready;

            var row = await RowAsync(Container("whisper"));

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("Running and answering", row.Found);
        }

        [Fact]
        public async Task Llama_passes_when_it_lists_every_model_in_the_chain_and_the_active_config()
        {
            var qwen = await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var gemma = await AddLlmAsync("Gemma", Llama, "gemma-12b");
            await SetChainAsync(gemma);
            _services.StatusByName["llama"] = AiServiceStatus.Ready;
            _http.Models(Llama, "gemma-12b", "qwen-28b", "qwen-9b");

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Contains("gemma-12b, qwen-28b", row.Expected);
        }

        [Fact]
        public async Task Llama_fails_naming_a_model_it_does_not_list()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            var typo = await AddLlmAsync("Typo", Llama, "gema-12b");
            await SetChainAsync(typo);
            _services.StatusByName["llama"] = AiServiceStatus.Ready;
            _http.Models(Llama, "gemma-12b", "qwen-28b");

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("llama doesn't list gema-12b", row.Found);
            Assert.Equal("Infra/llama/config/models.ini", row.Fix.Copy);
            Assert.Equal("settings/llm", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task Audiocpp_fails_naming_a_model_id_it_does_not_list()
        {
            await AddTtsAsync(AudioCpp, "breeze-q8");
            await AddVoiceDesignAsync(AudioCpp, "qwen3-design");
            _services.StatusByName["audiocpp"] = AiServiceStatus.Ready;
            _http.Models(AudioCpp, "breeze-q8", "breeze-design");

            var row = await RowAsync(Container("audiocpp"));

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("audiocpp doesn't list qwen3-design", row.Found);
            Assert.Equal("Infra/audiocpp/server.json", row.Fix.Copy);
        }

        [Fact]
        public async Task Audiocpp_passes_when_it_lists_every_tts_and_voice_design_model()
        {
            await AddTtsAsync(AudioCpp, "breeze-q8");
            await AddVoiceDesignAsync(AudioCpp, "breeze-design");
            _services.StatusByName["audiocpp"] = AiServiceStatus.Ready;
            _http.Models(AudioCpp, "breeze-q8", "breeze-design", "voxcpm2");

            var row = await RowAsync(Container("audiocpp"));

            Assert.Equal(SetupCheckResult.Passed, row.Result);
        }

        [Fact]
        public async Task A_model_list_that_fails_to_answer_fails_the_container()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _services.StatusByName["llama"] = AiServiceStatus.Ready;

            var row = await RowAsync(Container("llama"));

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.StartsWith($"GET {Llama}/v1/models failed", row.Found);
        }

        [Fact]
        public async Task A_watchdog_turned_off_adds_a_note_on_the_docker_rows()
        {
            await AddLlmAsync("Qwen", Llama, "qwen-28b");
            _watchdog.Enabled = false;

            var report = await CheckAsync();

            Assert.All(
                report.Rows.Where(r => r.Group == SetupCheckGroup.DockerServices),
                r => Assert.Equal("AiWatchdog:Enabled is false: a wedged container won't be restarted", r.Note));
        }

        [Fact]
        public async Task The_quick_check_never_starts_stops_or_restarts_a_container()
        {
            await AddEveryConfigAsync();
            foreach (var status in new[] { AiServiceStatus.Stopped, AiServiceStatus.NotFound, AiServiceStatus.Down, AiServiceStatus.Starting })
            {
                _services.StatusResult = status;
                await CheckAsync();
            }

            Assert.Empty(_services.Ops);
        }
    }
}
