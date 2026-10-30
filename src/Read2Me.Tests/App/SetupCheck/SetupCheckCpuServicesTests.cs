using System.ComponentModel;
using System.Net;
using System.Text.Json;
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
    /// The Full check's CPU services through <see cref="ISetupCheckRunner"/>: whisper transcribes the
    /// bundled sample clip, the similarity service scores the transcript against the clip's text,
    /// each container started if stopped and put back afterwards; unmanaged URLs are only sent the request.
    /// </summary>
    public class SetupCheckCpuServicesTests : AppDbTestBase, IDisposable
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        private const string Whisper = "http://localhost:9000";
        private const string MiniLm = "http://localhost:8200";

        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "r2me-setup-check", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _config = new();
        private readonly FakeAiServiceControl _services = new();
        private readonly FakeCpuServices _http = new();

        public SetupCheckCpuServicesTests()
        {
            Directory.CreateDirectory(_workspace);
            _config["Workspace:FolderPath"] = _workspace;
            foreach (var service in new DockerAiServiceRegistry().All)
                _services.ResolveByUrl[service.BaseUrl] = service;
            _services.StatusResult = AiServiceStatus.Ready;
        }

        public void Dispose()
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }

        // ── Fakes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Whisper's <c>POST /inference</c> and the similarity service's <c>POST /similarity</c>, by base
        /// URL; a URL with no answer refuses the connection. Records each request and the similarity texts.
        /// </summary>
        private sealed class FakeCpuServices : HttpMessageHandler, IHttpClientFactory
        {
            public Dictionary<string, string> Transcripts { get; } = new();
            public Dictionary<string, double> Scores { get; } = new();
            public List<string> Requests { get; } = [];
            public List<(string Text1, string Text2)> Scored { get; } = [];

            /// <summary>Called as each request arrives, which is how a test acts mid-step.</summary>
            public Action? OnRequest { get; set; }

            public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                Requests.Add($"{request.Method} {url}");
                OnRequest?.Invoke();
                var baseUrl = request.RequestUri.GetLeftPart(UriPartial.Authority);
                if (url.EndsWith("/inference") && Transcripts.TryGetValue(baseUrl, out var text))
                    return Json(JsonSerializer.Serialize(new { text }));
                if (url.EndsWith("/similarity") && Scores.TryGetValue(baseUrl, out var score))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    Scored.Add((body.RootElement.GetProperty("text1").GetString()!, body.RootElement.GetProperty("text2").GetString()!));
                    return Json(JsonSerializer.Serialize(new { similarity = score }));
                }
                throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            }

            private static HttpResponseMessage Json(string body) =>
                new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }

        private sealed class EncodesTone : IFfmpegEncodeCheck
        {
            public Task<FfmpegProbeResult> EncodeToneAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "Encoded a 1 s tone"));

            public Task<FfmpegDurationResult> MeasureAudioAsync(Stream audio, string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegDurationResult(TimeSpan.FromSeconds(1)));
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
                new AnsweringFfmpeg(),
                new AnsweringDocker(),
                new WorkspaceStart(configuration),
                SetupCheckTestServices.Create(Factory, configuration, _services, _http),
                SetupCheckTestServices.Full(watchdog: watchdog, encode: new EncodesTone()));
        }

        /// <summary>A Quick check, then a Full check to its end.</summary>
        private async Task<SetupCheckReport> FullCheckAsync()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            var start = await runner.StartFullCheckAsync();
            Assert.Null(start.Refusal);
            return await start.Finished.WaitAsync(Wait);
        }

        private static SetupCheckRow Row(SetupCheckReport report, string id) => report.Rows.Single(r => r.Id == id);

        private Task AddTranscriptionAsync(string baseUrl) =>
            new TranscriptionSettingsService(Factory, NullLogger<TranscriptionSettingsService>.Instance).CreateConfigAsync(
                new TranscriptionServiceConfig
                {
                    Name = "Whisper",
                    Type = TranscriptionServiceType.LocalWhisper,
                    SettingsJson = $$"""{"BaseUrl":"{{baseUrl}}"}""",
                });

        private Task AddSimilarityAsync(string baseUrl, double passThreshold = 0.85) =>
            new SemanticSimilaritySettingsService(Factory, NullLogger<SemanticSimilaritySettingsService>.Instance).CreateConfigAsync(
                new SemanticSimilarityServiceConfig
                {
                    Name = "MiniLM",
                    Type = SemanticSimilarityServiceType.MiniLmL6,
                    SettingsJson = $$"""{"BaseUrl":"{{baseUrl}}","PassThreshold":{{passThreshold}}}""",
                });

        // ── Transcription ────────────────────────────────────────────────────

        [Fact]
        public async Task Transcription_passes_when_the_sample_clip_transcribes_to_text()
        {
            await AddTranscriptionAsync(Whisper);
            _http.Transcripts[Whisper] = " It is a truth universally acknowledged. ";

            var row = Row(await FullCheckAsync(), SetupCheckIds.Transcription);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal(SetupCheckTier.Full, row.Tier);
            Assert.Equal("Transcribed the sample clip: \"It is a truth universally acknowledged.\"", row.Found);
            Assert.Contains($"POST {Whisper}/inference", _http.Requests);
        }

        [Fact]
        public async Task Transcription_fails_on_an_empty_transcript_but_whisper_answered_so_it_is_put_back()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.Stopped;
            _http.Transcripts[Whisper] = "  ";

            var report = await FullCheckAsync();

            var row = Row(report, SetupCheckIds.Transcription);
            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Transcribed the sample clip to empty text", row.Found);
            Assert.Equal("settings/transcription", row.Fix.Link?.Path);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Container("whisper")).Result);
            Assert.Equal(["start:whisper", "shutdown:whisper"], _services.OpLog);
            Assert.Equal("Put back: read2me-whisper stopped again.", report.PutBack);
        }

        [Fact]
        public async Task A_whisper_that_doesn_t_answer_fails_both_rows_and_is_left_as_it_is_for_its_logs()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.Stopped;

            var report = await FullCheckAsync();

            var row = Row(report, SetupCheckIds.Transcription);
            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("docker logs -f read2me-whisper", row.Fix.Copy);
            var container = Row(report, SetupCheckIds.Container("whisper"));
            Assert.Equal(SetupCheckResult.Failed, container.Result);
            Assert.Equal("Running, but didn't answer the check: left as it is for its logs", container.Found);
            Assert.Equal(["start:whisper"], _services.OpLog);
            Assert.Equal("Put back: read2me-whisper left as it is (its check failed).", report.PutBack);
        }

        [Fact]
        public async Task Transcription_fails_when_whisper_doesn_t_answer()
        {
            await AddTranscriptionAsync(Whisper);

            var row = Row(await FullCheckAsync(), SetupCheckIds.Transcription);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.StartsWith($"POST {Whisper}/inference failed: No connection could be made", row.Found);
        }

        // ── Similarity ───────────────────────────────────────────────────────

        [Fact]
        public async Task Similarity_scores_the_transcript_against_the_clip_s_known_text_and_passes_at_the_threshold()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm, passThreshold: 0.9);
            _http.Transcripts[Whisper] = "It is a truth universally acknowledged.";
            _http.Scores[MiniLm] = 0.9;

            var row = Row(await FullCheckAsync(), SetupCheckIds.Similarity);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal(SetupCheckTier.Full, row.Tier);
            Assert.Equal("Scored 0.90 against the clip's text (pass threshold 0.90)", row.Found);
            var (known, transcript) = Assert.Single(_http.Scored);
            Assert.Equal(
                "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.",
                known);
            Assert.Equal("It is a truth universally acknowledged.", transcript);
        }

        [Fact]
        public async Task Similarity_fails_below_the_config_s_own_pass_threshold()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm, passThreshold: 0.85);
            _http.Transcripts[Whisper] = "Something else entirely.";
            _http.Scores[MiniLm] = 0.42;

            var row = Row(await FullCheckAsync(), SetupCheckIds.Similarity);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Scored 0.42, below the config's pass threshold 0.85", row.Found);
            Assert.Equal("settings/similarity", row.Fix.Link?.Path);
        }

        [Fact]
        public async Task A_failed_transcription_doesn_t_fail_similarity_which_scores_the_clip_s_text_against_itself()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
            _http.Scores[MiniLm] = 1.0;

            var report = await FullCheckAsync();

            Assert.Equal(SetupCheckResult.Failed, Row(report, SetupCheckIds.Transcription).Result);
            var similarity = Row(report, SetupCheckIds.Similarity);
            Assert.Equal(SetupCheckResult.Passed, similarity.Result);
            Assert.Equal("The transcription check gave no transcript, so the clip's text was scored against itself.", similarity.Note);
            var (known, scored) = Assert.Single(_http.Scored);
            Assert.Equal(known, scored);
        }

        // ── Order and containers ─────────────────────────────────────────────

        [Fact]
        public async Task CPU_services_run_whisper_then_similarity_each_started_if_stopped_and_put_back_afterwards()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
            _services.StatusByName["whisper"] = AiServiceStatus.Stopped;
            _services.StatusByName["minilm-l6"] = AiServiceStatus.Stopped;
            _http.Transcripts[Whisper] = "It is a truth universally acknowledged.";
            _http.Scores[MiniLm] = 0.95;

            var report = await FullCheckAsync();

            Assert.Equal(["start:whisper", "start:minilm-l6", "shutdown:whisper", "shutdown:minilm-l6"], _services.OpLog);
            Assert.Equal($"POST {Whisper}/inference", _http.Requests[0]);
            Assert.Equal($"POST {MiniLm}/similarity", _http.Requests[1]);
            Assert.Equal("Put back: read2me-whisper stopped again; read2me-minilm-l6 stopped again.", report.PutBack);
            var whisper = Row(report, SetupCheckIds.Container("whisper"));
            Assert.Equal(SetupCheckResult.Passed, whisper.Result);
            Assert.Equal("Started for the Full check, and answered", whisper.Found);
        }

        [Fact]
        public async Task A_running_container_is_left_running()
        {
            await AddTranscriptionAsync(Whisper);
            _http.Transcripts[Whisper] = "It is a truth.";

            var report = await FullCheckAsync();

            Assert.Empty(_services.OpLog);
            Assert.Equal("Put back: read2me-whisper left running, as it was.", report.PutBack);
        }

        [Fact]
        public async Task A_container_that_won_t_start_fails_its_rows_without_a_request_and_is_left_as_it_is()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.Stopped;
            _services.StartResultByName["whisper"] = new AiServiceOpResult(false, AiServiceStatus.Stopped, "health never answered");

            var report = await FullCheckAsync();

            var container = Row(report, SetupCheckIds.Container("whisper"));
            Assert.Equal(SetupCheckResult.Failed, container.Result);
            Assert.Equal("Didn't start: health never answered", container.Found);
            Assert.Equal("Not sent: read2me-whisper isn't ready", Row(report, SetupCheckIds.Transcription).Found);
            Assert.Empty(_http.Requests);
            Assert.Equal(["start:whisper"], _services.OpLog);
            Assert.Equal("Put back: read2me-whisper left as it is (its check failed).", report.PutBack);
        }

        [Fact]
        public async Task A_missing_container_fails_with_the_compose_command()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.NotFound;

            var container = Row(await FullCheckAsync(), SetupCheckIds.Container("whisper"));

            Assert.Equal(SetupCheckResult.Failed, container.Result);
            Assert.Equal("docker compose up -d whisper", container.Fix.Copy);
            Assert.Empty(_services.OpLog);
        }

        [Fact]
        public async Task An_unmanaged_URL_is_sent_the_same_request_but_never_started_stopped_or_put_back()
        {
            const string remote = "http://whisper-box:9000";
            await AddTranscriptionAsync(remote);
            _http.Transcripts[remote] = "It is a truth.";

            var report = await FullCheckAsync();

            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Transcription).Result);
            Assert.Contains($"POST {remote}/inference", _http.Requests);
            Assert.Empty(_services.OpLog);
            Assert.Equal(0, _services.StatusCalls);
            Assert.Equal("Nothing to put back: the check drove no containers.", report.PutBack);
        }

        [Fact]
        public async Task A_check_with_no_active_config_gets_no_step_and_stays_Not_used()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var start = await runner.StartFullCheckAsync();
            var report = await start.Finished.WaitAsync(Wait);

            Assert.Equal(1, start.Report.FullRun?.Total);
            Assert.Equal(SetupCheckResult.NotUsed, Row(report, SetupCheckIds.Transcription).Result);
            Assert.Equal(SetupCheckResult.NotUsed, Row(report, SetupCheckIds.Similarity).Result);
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task The_watchdog_holds_off_the_CPU_containers_the_steps_drive()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
            var watchdog = new RecordingHoldOff();
            var runner = Runner(watchdog);
            await runner.RunQuickCheckAsync(CancellationToken.None);

            await (await runner.StartFullCheckAsync()).Finished.WaitAsync(Wait);

            Assert.Equal(["whisper", "minilm-l6"], Assert.Single(watchdog.Holds));
        }

        [Fact]
        public async Task A_container_still_starting_is_waited_for_and_left_running()
        {
            await AddTranscriptionAsync(Whisper);
            _services.StatusByName["whisper"] = AiServiceStatus.Starting;
            _http.Transcripts[Whisper] = "It is a truth.";

            var report = await FullCheckAsync();

            Assert.Equal(["start:whisper"], _services.OpLog);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Transcription).Result);
            Assert.Equal("Put back: read2me-whisper left running, as it was.", report.PutBack);
        }

        [Fact]
        public async Task A_cancelled_run_still_puts_back_what_it_started_and_the_rest_read_Not_run()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
            _services.StatusByName["whisper"] = AiServiceStatus.Stopped;
            _http.Transcripts[Whisper] = "It is a truth.";
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            // Cancelled as whisper is asked to transcribe: that step finishes, similarity never runs.
            _http.OnRequest = () => runner.CancelFullCheck();

            var report = await (await runner.StartFullCheckAsync()).Finished.WaitAsync(Wait);

            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Transcription).Result);
            var similarity = Row(report, SetupCheckIds.Similarity);
            Assert.Equal(SetupCheckResult.NotRun, similarity.Result);
            Assert.Equal("Full check cancelled", similarity.Found);
            Assert.Equal(["start:whisper", "shutdown:whisper"], _services.OpLog);
            Assert.Equal("Put back: read2me-whisper stopped again.", report.PutBack);
        }

        // ── Re-run one check ─────────────────────────────────────────────────

        [Fact]
        public async Task Re_running_transcription_starts_only_whisper_and_puts_it_back()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
            _services.StatusByName["whisper"] = AiServiceStatus.Stopped;
            _services.StatusByName["minilm-l6"] = AiServiceStatus.Stopped;
            _http.Transcripts[Whisper] = "It is a truth universally acknowledged.";
            var runner = Runner();
            var quick = await runner.RunQuickCheckAsync(CancellationToken.None);
            Assert.Equal(new SetupRerun(Gpu: false), Row(quick, SetupCheckIds.Transcription).Rerun);

            var report = await (await runner.RerunCheckAsync(SetupCheckIds.Transcription)).Finished.WaitAsync(Wait);

            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Transcription).Result);
            Assert.Equal(Row(quick, SetupCheckIds.Similarity).Result, Row(report, SetupCheckIds.Similarity).Result);
            Assert.Equal([$"POST {Whisper}/inference"], _http.Requests);
            Assert.Equal(["start:whisper", "shutdown:whisper"], _services.OpLog);
            Assert.Equal("Put back: read2me-whisper stopped again.", report.PutBack);
        }

        [Fact]
        public async Task Re_running_similarity_transcribes_first_and_scores_that_transcript()
        {
            await AddTranscriptionAsync(Whisper);
            await AddSimilarityAsync(MiniLm);
            _http.Transcripts[Whisper] = "It is a truth universally acknowledged.";
            _http.Scores[MiniLm] = 0.9;
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var start = await runner.RerunCheckAsync(SetupCheckIds.Similarity);
            var report = await start.Finished.WaitAsync(Wait);

            Assert.Equal(2, start.Report.FullRun?.Total);
            Assert.Equal([$"POST {Whisper}/inference", $"POST {MiniLm}/similarity"], _http.Requests);
            Assert.Equal(SetupCheckResult.Passed, Row(report, SetupCheckIds.Transcription).Result);
            var similarity = Row(report, SetupCheckIds.Similarity);
            Assert.Equal(SetupCheckResult.Passed, similarity.Result);
            Assert.DoesNotContain("scored against itself", similarity.Note ?? "");
        }

        [Fact]
        public async Task Re_running_similarity_with_no_transcription_config_scores_the_clip_s_text_against_itself_and_says_so()
        {
            await AddSimilarityAsync(MiniLm);
            _http.Scores[MiniLm] = 1.0;
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var row = Row(await (await runner.RerunCheckAsync(SetupCheckIds.Similarity)).Finished.WaitAsync(Wait), SetupCheckIds.Similarity);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Contains("scored against itself", row.Note);
        }
    }
}
