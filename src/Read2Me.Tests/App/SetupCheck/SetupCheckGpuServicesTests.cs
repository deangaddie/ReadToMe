using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Health;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The Full check's GPU services through <see cref="ISetupCheckRunner"/>, one at a time after the
    /// CPU ones: audiocpp speaks a sentence with the sample clip as reference and designs a voice
    /// (each passing when ffprobe reads a duration), then llama answers a 1-token completion per
    /// distinct model. Every GPU visit sweeps the rival GPU container first; afterwards containers
    /// are put back as found, llama with the model it had, and a failed one is left as it is.
    /// </summary>
    public class SetupCheckGpuServicesTests : AppDbTestBase, IDisposable
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        private const string Llama = "http://localhost:8080";
        private const string AudioCpp = "http://localhost:8004";
        private const string Whisper = "http://localhost:9000";

        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "r2me-setup-check", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _config = new();
        private readonly StatefulControl _services = new();
        private readonly FakeGpuServices _http = new();
        private readonly MeasuringEncode _encode = new();

        public SetupCheckGpuServicesTests()
        {
            Directory.CreateDirectory(_workspace);
            _config["Workspace:FolderPath"] = _workspace;
        }

        public void Dispose()
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }

        // ── Fakes ────────────────────────────────────────────────────────────

        /// <summary>
        /// The container lifecycle as docker would leave it: a start makes a service Ready, a shutdown
        /// makes it Stopped. Records each op, "start:llama"-style.
        /// </summary>
        private sealed class StatefulControl : IAiServiceControl
        {
            private readonly DockerAiServiceRegistry _registry = new();

            public Dictionary<string, AiServiceStatus> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> StartErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> OpLog { get; } = [];

            public DockerAiService? Resolve(string baseUrl) =>
                _registry.TryGetByBaseUrl(baseUrl, out var service) ? service : null;

            public Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct) =>
                Task.FromResult(Status.TryGetValue(service.Name, out var status) ? status : AiServiceStatus.Stopped);

            public Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct)
            {
                OpLog.Add($"start:{service.Name}");
                if (StartErrors.TryGetValue(service.Name, out var error))
                    return Task.FromResult(new AiServiceOpResult(false, AiServiceStatus.Stopped, error));
                Status[service.Name] = AiServiceStatus.Ready;
                return Task.FromResult(new AiServiceOpResult(true, AiServiceStatus.Ready, null));
            }

            public Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct) =>
                StartAsync(service, ct);

            public Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct)
            {
                OpLog.Add($"shutdown:{service.Name}");
                Status[service.Name] = AiServiceStatus.Stopped;
                return Task.FromResult(new AiServiceOpResult(true, AiServiceStatus.Stopped, null));
            }
        }

        /// <summary>
        /// llama (<c>GET /v1/models</c> with <c>status.value</c>, <c>POST /v1/chat/completions</c>, which
        /// loads the model it names), audio.cpp (<c>GET /v1/models</c>, <c>POST /v1/audio/speech</c>) and
        /// whisper's <c>POST /inference</c>. A model in an *Errors map answers 500 with that body.
        /// </summary>
        private sealed class FakeGpuServices : HttpMessageHandler, IHttpClientFactory
        {
            public string? LoadedLlama { get; set; }
            public string LlamaContent { get; set; } = "OK";
            public Dictionary<string, string> LlamaErrors { get; } = new();
            public Dictionary<string, string> SpeechErrors { get; } = new();

            /// <summary>Models whose request gets no answer at all, as from a container that died mid-request.</summary>
            public HashSet<string> Unanswered { get; } = [];

            /// <summary>Each POST, "POST http://localhost:8080/v1/chat/completions"-style.</summary>
            public List<string> Posts { get; } = [];
            public List<JsonObject> ChatBodies { get; } = [];
            public List<JsonObject> SpeechBodies { get; } = [];

            /// <summary>Called as each speech request arrives, which is how a test acts mid-step.</summary>
            public Action? OnSpeech { get; set; }

            public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var baseUrl = request.RequestUri!.GetLeftPart(UriPartial.Authority);
                var path = request.RequestUri.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/v1/models")
                    return baseUrl == Llama
                        ? Json(new { data = new[] { "qwen-28b", "gemma-12b" }.Select(id => new { id, status = new { value = id == LoadedLlama ? "loaded" : "unloaded" } }) })
                        : Json(new { data = new[] { "breeze-q8", "breeze-design" }.Select(id => new { id, loaded = false }) });

                Posts.Add($"POST {baseUrl}{path}");
                if (baseUrl == Whisper && path == "/inference")
                    return Json(new { text = "It is a truth universally acknowledged." });

                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                var model = body["model"]?.GetValue<string>() ?? "";
                if (baseUrl == Llama && path == "/v1/chat/completions")
                {
                    ChatBodies.Add(body);
                    ThrowIfUnanswered(model);
                    if (LlamaErrors.TryGetValue(model, out var error))
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(error) };
                    LoadedLlama = model;
                    return Json(new { choices = new[] { new { message = new { role = "assistant", content = LlamaContent } } } });
                }
                if (baseUrl == AudioCpp && path == "/v1/audio/speech")
                {
                    SpeechBodies.Add(body);
                    OnSpeech?.Invoke();
                    ThrowIfUnanswered(model);
                    if (SpeechErrors.TryGetValue(model, out var error))
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(error) };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("RIFF____WAVEfmt ")) };
                }
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            }

            private void ThrowIfUnanswered(string model)
            {
                if (Unanswered.Contains(model))
                    throw new HttpRequestException("An existing connection was forcibly closed by the remote host.");
            }

            private static HttpResponseMessage Json(object value) =>
                new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }

        /// <summary>ffprobe reading <see cref="Result"/> from any audio; the encode passes.</summary>
        private sealed class MeasuringEncode : IFfmpegEncodeCheck
        {
            public FfmpegDurationResult Result { get; set; } = new(TimeSpan.FromSeconds(1.5));

            public Task<FfmpegProbeResult> EncodeToneAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "Encoded a 1 s tone"));

            public Task<FfmpegDurationResult> MeasureAudioAsync(Stream audio, string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(Result);
        }

        private sealed class AnsweringFfmpeg : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffmpeg version 7.1"));

            public Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffprobe version 7.1"));
        }

        private sealed class AnsweringDocker : IProcessRunner
        {
            public Task<(int ExitCode, string Output)> RunAsync(
                string fileName, string arguments, TimeSpan timeout, CancellationToken ct) =>
                fileName == "docker"
                    ? Task.FromResult((0, "Docker version 28.0.1"))
                    : throw new Win32Exception(2, "not found");
        }

        // ── Builders ─────────────────────────────────────────────────────────

        private SetupCheckRunner Runner(RecordingHoldOff? watchdog = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(_config).Build();
            return new(
                configuration,
                @"C:\app",
                Factory,
                new AnsweringFfmpeg(),
                new AnsweringDocker(),
                new WorkspaceStart(configuration),
                SetupCheckTestServices.Create(Factory, _services, _http),
                SetupCheckTestServices.Full(watchdog: watchdog, encode: _encode));
        }

        /// <summary>A Quick check, then a Full check to its end.</summary>
        private async Task<SetupCheckReport> FullCheckAsync(RecordingHoldOff? watchdog = null)
        {
            var runner = Runner(watchdog);
            await runner.RunQuickCheckAsync(CancellationToken.None);
            var start = await runner.StartFullCheckAsync();
            Assert.Null(start.Refusal);
            return await start.Finished.WaitAsync(Wait);
        }

        private static SetupCheckRow Row(SetupCheckReport report, string id) => report.Rows.Single(r => r.Id == id);

        private LlmSettingsService LlmSettings => new(Factory, NullLogger<LlmSettingsService>.Instance);

        private async Task<LlmServerConfig> AddLlmAsync(string name, string model, bool active = false)
        {
            var config = await LlmSettings.CreateConfigAsync(new LlmServerConfig { Name = name, BaseUrl = Llama, Model = model });
            if (active)
                await LlmSettings.SetActiveConfigAsync(config.Id);
            return config;
        }

        private Task ChainAsync(params LlmServerConfig[] configs) =>
            LlmSettings.SetAttributionChainEntriesAsync([.. configs.Select((c, i) => new AttributionChainEntry(c.Id, Thinking: i > 0))]);

        private Task AddTtsAsync(string model = "breeze-q8") =>
            new ParagraphTtsSettingsService(Factory, NullLogger<ParagraphTtsSettingsService>.Instance).CreateConfigAsync(
                new ParagraphTtsServiceConfig
                {
                    Name = "Breeze",
                    Type = ParagraphTtsServiceType.Breeze,
                    SettingsJson = $$"""{"baseUrl":"{{AudioCpp}}","modelId":"{{model}}"}""",
                });

        private Task AddVoiceDesignAsync(string model = "breeze-design") =>
            new VoiceDesignSettingsService(Factory, NullLogger<VoiceDesignSettingsService>.Instance).CreateConfigAsync(
                new VoiceDesignServiceConfig
                {
                    Name = "Breeze design",
                    Type = VoiceDesignServiceType.Breeze,
                    SettingsJson = $$"""{"baseUrl":"{{AudioCpp}}","modelId":"{{model}}"}""",
                });

        private Task AddTranscriptionAsync() =>
            new TranscriptionSettingsService(Factory, NullLogger<TranscriptionSettingsService>.Instance).CreateConfigAsync(
                new TranscriptionServiceConfig
                {
                    Name = "Whisper",
                    Type = TranscriptionServiceType.LocalWhisper,
                    SettingsJson = $$"""{"BaseUrl":"{{Whisper}}"}""",
                });

        // ── Paragraph TTS and voice design ───────────────────────────────────

        [Fact]
        public async Task Paragraph_TTS_speaks_a_short_sentence_with_the_sample_clip_as_reference_and_passes_on_a_duration()
        {
            await AddTtsAsync();
            _services.Status["audiocpp"] = AiServiceStatus.Ready;

            var report = await FullCheckAsync();

            var row = Row(report, SetupCheckIds.ParagraphTts);
            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal(SetupCheckTier.Full, row.Tier);
            Assert.Equal("Spoke 1.5 s of audio", row.Found);
            var body = Assert.Single(_http.SpeechBodies);
            Assert.Equal("breeze-q8", body["model"]!.GetValue<string>());
            Assert.Equal("It is a truth universally acknowledged.", body["input"]!.GetValue<string>());
            Assert.Equal(SampleClip.Text, body["reference_text"]!.GetValue<string>());
            using var clip = new MemoryStream();
            await using (var wav = SampleClip.OpenWav())
                await wav.CopyToAsync(clip);
            Assert.Equal(Convert.ToBase64String(clip.ToArray()), body["voice_ref"]!["data"]!.GetValue<string>());
        }

        [Fact]
        public async Task Paragraph_TTS_fails_when_ffprobe_reads_no_length_from_the_audio()
        {
            await AddTtsAsync();
            _encode.Result = new FfmpegDurationResult(TimeSpan.Zero);

            var row = Row(await FullCheckAsync(), SetupCheckIds.ParagraphTts);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Spoke audio that ffprobe reads as 0 s long", row.Found);
            Assert.Equal("settings/tts", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task Paragraph_TTS_fails_toward_the_ffmpeg_setting_when_ffprobe_can_t_read_the_audio()
        {
            await AddTtsAsync();
            _encode.Result = new FfmpegDurationResult(null, "ffprobe failed: Invalid data found when processing input");

            var row = Row(await FullCheckAsync(), SetupCheckIds.ParagraphTts);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("ffprobe couldn't read the audio: ffprobe failed: Invalid data found when processing input", row.Found);
            Assert.Equal("settings/audio", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task Voice_design_generates_from_a_fixed_prompt_and_the_sample_text()
        {
            await AddVoiceDesignAsync();

            var row = Row(await FullCheckAsync(), SetupCheckIds.VoiceDesign);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal(SetupCheckTier.Full, row.Tier);
            Assert.Equal("Designed 1.5 s of audio", row.Found);
            var body = Assert.Single(_http.SpeechBodies);
            Assert.Equal("breeze-design", body["model"]!.GetValue<string>());
            Assert.Equal(SampleClip.Text, body["input"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(body["options"]!["instruction"]!.GetValue<string>()));
            Assert.Null(body["voice_ref"]);
        }

        // ── LLM ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Each_distinct_model_in_the_chain_and_the_active_config_gets_one_1_token_completion_with_thinking_off()
        {
            var qwen = await AddLlmAsync("Qwen", "qwen-28b", active: true);
            var gemma = await AddLlmAsync("Gemma", "gemma-12b");
            await ChainAsync(gemma, qwen, gemma);

            var report = await FullCheckAsync();

            Assert.Equal(["qwen-28b", "gemma-12b"], _http.ChatBodies.Select(b => b["model"]!.GetValue<string>()));
            Assert.All(_http.ChatBodies, body =>
            {
                Assert.Equal(1, body["max_tokens"]!.GetValue<int>());
                Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
                Assert.False(body["stream"]!.GetValue<bool>());
            });
            var llm = Row(report, SetupCheckIds.Llm);
            Assert.Equal(SetupCheckResult.Passed, llm.Result);
            Assert.Equal("qwen-28b answered \"OK\"", llm.Found);
            var chain = Row(report, SetupCheckIds.AttributionChain);
            Assert.Equal(SetupCheckResult.Passed, chain.Result);
            Assert.Equal("qwen-28b answered \"OK\"; gemma-12b answered \"OK\"", chain.Found);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("llama")).Result);
        }

        [Fact]
        public async Task An_LLM_that_answers_200_with_no_content_fails_its_row_but_not_llama()
        {
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            _http.LlamaContent = "";

            var report = await FullCheckAsync();

            var llm = Row(report, SetupCheckIds.Llm);
            Assert.Equal(SetupCheckResult.Failed, llm.Result);
            Assert.Equal("qwen-28b answered 200 with no content", llm.Found);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("llama")).Result);
        }

        [Fact]
        public async Task One_model_failing_doesn_t_stop_the_next_from_being_exercised()
        {
            var qwen = await AddLlmAsync("Qwen", "qwen-28b", active: true);
            var gemma = await AddLlmAsync("Gemma", "gemma-12b");
            await ChainAsync(qwen, gemma);
            _http.LlamaErrors["qwen-28b"] = "model name=qwen-28b failed to load";

            var report = await FullCheckAsync();

            Assert.Equal(["qwen-28b", "gemma-12b"], _http.ChatBodies.Select(b => b["model"]!.GetValue<string>()));
            var chain = Row(report, SetupCheckIds.AttributionChain);
            Assert.Equal(SetupCheckResult.Failed, chain.Result);
            Assert.Contains("gemma-12b answered \"OK\"", chain.Found);
            // llama answered both: a model that won't load is the config's fault, not the container's.
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("llama")).Result);
        }

        // ── Order, sweep and putting back ────────────────────────────────────

        [Fact]
        public async Task GPU_services_run_after_the_CPU_ones_audiocpp_s_models_then_llama_s()
        {
            await AddTranscriptionAsync();
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            await AddTtsAsync();
            await AddVoiceDesignAsync();
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var start = await runner.StartFullCheckAsync();
            await start.Finished.WaitAsync(Wait);

            Assert.Equal(
                [
                    $"POST {Whisper}/inference",
                    $"POST {AudioCpp}/v1/audio/speech",
                    $"POST {AudioCpp}/v1/audio/speech",
                    $"POST {Llama}/v1/chat/completions",
                ],
                _http.Posts);
            Assert.Equal(["breeze-q8", "breeze-design"], _http.SpeechBodies.Select(b => b["model"]!.GetValue<string>()));
            Assert.Equal(5, start.Report.FullRun?.Total);
        }

        [Fact]
        public async Task Each_GPU_visit_sweeps_the_rival_GPU_container_and_putting_back_restores_llama_with_its_model()
        {
            await AddTtsAsync();
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            _services.Status["llama"] = AiServiceStatus.Ready;
            _http.LoadedLlama = "gemma-12b";

            var report = await FullCheckAsync();

            Assert.Equal(["shutdown:llama", "start:audiocpp", "shutdown:audiocpp", "start:llama"], _services.OpLog);
            Assert.Equal(AiServiceStatus.Ready, _services.Status["llama"]);
            Assert.Equal(AiServiceStatus.Stopped, _services.Status["audiocpp"]);
            Assert.Equal(["qwen-28b", "gemma-12b"], _http.ChatBodies.Select(b => b["model"]!.GetValue<string>()));
            Assert.Equal("gemma-12b", _http.LoadedLlama);
            Assert.Equal(
                "Put back: read2me-llama left running, as it was, with gemma-12b loaded again; read2me-audiocpp stopped again.",
                report.PutBack);
        }

        [Fact]
        public async Task A_GPU_container_swept_and_not_visited_again_is_started_again_with_its_model()
        {
            await AddTtsAsync();
            _services.Status["llama"] = AiServiceStatus.Ready;
            _http.LoadedLlama = "qwen-28b";

            var report = await FullCheckAsync();

            Assert.Equal(["shutdown:llama", "start:audiocpp", "shutdown:audiocpp", "start:llama"], _services.OpLog);
            Assert.Equal(
                "Put back: read2me-llama started again, with qwen-28b loaded again; read2me-audiocpp stopped again.",
                report.PutBack);
        }

        [Fact]
        public async Task A_failed_container_is_left_as_it_was_found()
        {
            await AddTtsAsync();
            _http.Unanswered.Add("breeze-q8");

            var report = await FullCheckAsync();

            var container = Row(report, SetupCheckIds.Container("audiocpp"));
            Assert.Equal(SetupCheckResult.Failed, container.Result);
            Assert.Equal("docker logs -f read2me-audiocpp", container.Fix.Copy);
            Assert.Equal(["start:audiocpp"], _services.OpLog);
            Assert.Equal("Put back: read2me-audiocpp left as it is (its check failed).", report.PutBack);
        }

        [Fact]
        public async Task A_failed_llama_isn_t_asked_to_reload_its_model()
        {
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            _services.Status["llama"] = AiServiceStatus.Ready;
            _http.LoadedLlama = "gemma-12b";
            _http.Unanswered.Add("qwen-28b");

            var report = await FullCheckAsync();

            Assert.Equal(SetupCheckResult.Failed, Row(report, SetupCheckIds.Container("llama")).Result);
            Assert.DoesNotContain(_http.ChatBodies, b => b["model"]!.GetValue<string>() == "gemma-12b");
            Assert.Equal("Put back: read2me-llama left as it is (its check failed).", report.PutBack);
        }

        [Fact]
        public async Task A_model_that_won_t_load_fails_only_its_config_rows_and_llama_reloads_the_model_it_had()
        {
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            _services.Status["llama"] = AiServiceStatus.Ready;
            _http.LoadedLlama = "gemma-12b";
            _http.LlamaErrors["qwen-28b"] = "model name=qwen-28b failed to load";

            var report = await FullCheckAsync();

            Assert.Equal(SetupCheckResult.Failed, Row(report, SetupCheckIds.Llm).Result);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("llama")).Result);
            Assert.Equal(["qwen-28b", "gemma-12b"], _http.ChatBodies.Select(b => b["model"]!.GetValue<string>()));
            Assert.Equal("Put back: read2me-llama left running, as it was, with gemma-12b loaded again.", report.PutBack);
        }

        [Fact]
        public async Task A_failed_GPU_container_a_later_sweep_stops_is_left_stopped_and_the_line_says_why()
        {
            await AddTtsAsync();
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            _http.Unanswered.Add("breeze-q8");

            var report = await FullCheckAsync();

            Assert.Equal(["start:audiocpp", "shutdown:audiocpp", "start:llama", "shutdown:llama"], _services.OpLog);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Llm).Result);
            Assert.Equal(
                "Put back: read2me-audiocpp left stopped (its check failed; a later GPU check's sweep stopped it, and docker logs still reads it); read2me-llama stopped again.",
                report.PutBack);
        }

        [Fact]
        public async Task The_watchdog_holds_off_every_GPU_container_a_sweep_may_stop()
        {
            await AddTtsAsync();
            var watchdog = new RecordingHoldOff();

            await FullCheckAsync(watchdog);

            Assert.Equal(["llama", "audiocpp"], Assert.Single(watchdog.Holds));
        }

        [Fact]
        public async Task A_GPU_container_isn_t_started_again_while_a_failed_one_holds_the_GPU()
        {
            await AddTtsAsync();
            _services.Status["llama"] = AiServiceStatus.Ready;
            _http.Unanswered.Add("breeze-q8");

            var report = await FullCheckAsync();

            Assert.Equal(["shutdown:llama", "start:audiocpp"], _services.OpLog);
            Assert.Equal(
                "Put back: read2me-llama not started again: read2me-audiocpp holds the GPU; read2me-audiocpp left as it is (its check failed).",
                report.PutBack);
        }

        [Fact]
        public async Task A_cancel_keeps_the_verdict_a_finished_step_gave_a_row_the_next_step_shares()
        {
            await AddTtsAsync();
            await AddVoiceDesignAsync();
            _http.Unanswered.Add("breeze-q8");
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            // Cancelled during paragraph TTS: voice design never runs, but the audiocpp row it shares keeps TTS's verdict.
            _http.OnSpeech = () => runner.CancelFullCheck();

            var report = await (await runner.StartFullCheckAsync()).Finished.WaitAsync(Wait);

            Assert.Equal(SetupCheckResult.NotRun, Row(report, SetupCheckIds.VoiceDesign).Result);
            Assert.Equal(SetupCheckResult.Failed, Row(report, SetupCheckIds.Container("audiocpp")).Result);
        }

        // ── Missing model files ──────────────────────────────────────────────

        [Fact]
        public async Task A_missing_audiocpp_model_file_s_hint_names_the_path_and_GGUF_MODELS_DIR()
        {
            const string path = "/models/Breeze-TTS-2-GGUF/breeze-tts-2-q8_0.gguf";
            await AddTtsAsync();
            _http.SpeechErrors["breeze-q8"] = $$$"""{"error":{"message":"model path does not exist: {{{path}}}"}}""";

            var report = await FullCheckAsync();

            var row = Row(report, SetupCheckIds.ParagraphTts);
            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Contains(path, row.Fix.Text);
            Assert.Contains("GGUF_MODELS_DIR", row.Fix.Text);
            Assert.Contains("Infra/.env", row.Fix.Text);
            Assert.Equal(path, row.Fix.Copy);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("audiocpp")).Result);
            Assert.Equal("Put back: read2me-audiocpp stopped again.", report.PutBack);
        }

        [Fact]
        public async Task A_llama_model_that_fails_to_load_points_at_its_preset_GGUF_MODELS_DIR_and_the_logs()
        {
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            _http.LlamaErrors["qwen-28b"] = """{"error":{"code":500,"message":"model name=qwen-28b failed to load"}}""";

            var row = Row(await FullCheckAsync(), SetupCheckIds.Llm);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Contains("failed to load", row.Found);
            // No path in the error, and it may not be the file: the hint hedges and points at the logs.
            Assert.Equal(
                "llama couldn't load qwen-28b: check that the GGUF its preset names in Infra/llama/config/models.ini is in the folder GGUF_MODELS_DIR names in Infra/.env (mounted at /models), then read why with docker logs -f read2me-llama.",
                row.Fix.Text);
            Assert.Equal("docker logs -f read2me-llama", row.Fix.Copy);
        }

        // ── Estimate ─────────────────────────────────────────────────────────

        [Fact]
        public async Task The_estimate_counts_a_model_load_for_each_model_exercised()
        {
            var qwen = await AddLlmAsync("Qwen", "qwen-28b", active: true);
            var one = (await Runner().RunQuickCheckAsync(CancellationToken.None)).FullCheckEstimateSeconds;

            var gemma = await AddLlmAsync("Gemma", "gemma-12b");
            await ChainAsync(qwen, gemma);
            var two = (await Runner().RunQuickCheckAsync(CancellationToken.None)).FullCheckEstimateSeconds;

            // A model switch takes 9–31 s on top of the request.
            Assert.InRange(two - one, 31, 120);
        }

        [Fact]
        public async Task The_estimate_counts_a_start_for_each_container_the_run_must_start()
        {
            await AddTtsAsync();
            _services.Status["audiocpp"] = AiServiceStatus.Ready;
            var running = (await Runner().RunQuickCheckAsync(CancellationToken.None)).FullCheckEstimateSeconds;

            _services.Status["audiocpp"] = AiServiceStatus.Stopped;
            var stopped = (await Runner().RunQuickCheckAsync(CancellationToken.None)).FullCheckEstimateSeconds;

            Assert.InRange(stopped - running, 10, 60);
        }

        [Fact]
        public async Task The_estimate_counts_a_running_llama_s_restart_and_its_model_reload()
        {
            await AddTtsAsync();
            _services.Status["audiocpp"] = AiServiceStatus.Ready;
            var without = (await Runner().RunQuickCheckAsync(CancellationToken.None)).FullCheckEstimateSeconds;

            // The TTS step's sweep stops llama; putting back starts it and reloads its model.
            _services.Status["llama"] = AiServiceStatus.Ready;
            var with = (await Runner().RunQuickCheckAsync(CancellationToken.None)).FullCheckEstimateSeconds;

            Assert.InRange(with - without, 10 + 31, 120);
        }

        // ── Re-run one check ─────────────────────────────────────────────────

        [Fact]
        public async Task Re_running_paragraph_TTS_sends_only_its_request_and_puts_the_swept_llama_back_with_its_model()
        {
            await AddTtsAsync();
            await AddLlmAsync("Qwen", "qwen-28b", active: true);
            await AddTranscriptionAsync();
            _services.Status["llama"] = AiServiceStatus.Ready;
            _http.LoadedLlama = "gemma-12b";
            var runner = Runner();
            var quick = await runner.RunQuickCheckAsync(CancellationToken.None);
            Assert.Equal(new SetupRerun(Gpu: true), Row(quick, SetupCheckIds.ParagraphTts).Rerun);

            var start = await runner.RerunCheckAsync(SetupCheckIds.ParagraphTts);
            Assert.Null(start.Refusal);
            Assert.Equal(1, start.Report.FullRun?.Total);
            var report = await start.Finished.WaitAsync(Wait);

            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.ParagraphTts).Result);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("audiocpp")).Result);
            Assert.Equal(Row(quick, SetupCheckIds.Transcription), Row(report, SetupCheckIds.Transcription));
            Assert.DoesNotContain(_http.Posts, p => p.StartsWith($"POST {Whisper}"));
            Assert.Equal(["shutdown:llama", "start:audiocpp", "shutdown:audiocpp", "start:llama"], _services.OpLog);
            // The only completion is llama loading the model it had again.
            Assert.Equal(["gemma-12b"], _http.ChatBodies.Select(b => b["model"]!.GetValue<string>()));
            Assert.Equal(
                "Put back: read2me-llama started again, with gemma-12b loaded again; read2me-audiocpp stopped again.",
                report.PutBack);
            Assert.Null(report.FullCheckedAt);
            Assert.Null(report.FullRun);
        }

        [Fact]
        public async Task Re_running_the_attribution_chain_exercises_only_the_chain_s_models()
        {
            await AddLlmAsync("Gemma", "gemma-12b", active: true);
            await ChainAsync(await AddLlmAsync("Qwen", "qwen-28b"));
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var report = await (await runner.RerunCheckAsync(SetupCheckIds.AttributionChain)).Finished.WaitAsync(Wait);

            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.AttributionChain).Result);
            Assert.Equal(["qwen-28b"], _http.ChatBodies.Select(b => b["model"]!.GetValue<string>()));
        }

        [Fact]
        public async Task A_check_on_a_URL_the_app_doesn_t_manage_re_runs_without_touching_a_GPU_container()
        {
            await LlmSettings.SetActiveConfigAsync(
                (await LlmSettings.CreateConfigAsync(new LlmServerConfig { Name = "Remote", BaseUrl = "http://llm-box:8080", Model = "qwen-28b" })).Id);

            var quick = await Runner().RunQuickCheckAsync(CancellationToken.None);

            Assert.Equal(new SetupRerun(Gpu: false), Row(quick, SetupCheckIds.Llm).Rerun);
        }

        [Fact]
        public async Task A_re_run_the_report_says_needs_no_GPU_is_refused_once_its_config_drives_one()
        {
            await LlmSettings.SetActiveConfigAsync(
                (await LlmSettings.CreateConfigAsync(new LlmServerConfig { Name = "Remote", BaseUrl = "http://llm-box:8080", Model = "qwen-28b" })).Id);
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            // The web app skipped its GPU confirm on the report's word; the config has moved since.
            await AddLlmAsync("Qwen", "qwen-28b", active: true);

            var start = await runner.RerunCheckAsync(SetupCheckIds.Llm);

            Assert.Equal("LLM now drives a GPU container: run the Quick check, then re-run it.", start.Refusal);
            Assert.Null(start.Report.FullRun);
            Assert.Empty(_services.OpLog);
        }
    }
}
