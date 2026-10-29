using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Live;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Health;
using Read2Me.Services.Queueing;
using Read2Me.Tests.App.Live;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Read2Me.Tests.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The Full check machinery through <see cref="ISetupCheckRunner"/>, proven with its one local
    /// step, the ffmpeg encode: refusal while work is in flight, the queues held and Preflight refused
    /// for the run, the watchdog held off, progress on the hub, joining and cancelling.
    /// </summary>
    public class SetupCheckFullCheckTests : AppDbTestBase, IDisposable
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "r2me-setup-check", Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _config = new();
        private readonly ProcessingGate<QueuedParagraph> _characterGate = new();
        private readonly ProcessingGate<QueuedAudioItem> _audioGate = new();
        private readonly SetupCheckBusy _busy = new();
        private readonly RecordingHoldOff _watchdog = new();
        private readonly RecordingHubContext _hub = new();
        private readonly HeldEncode _encode = new();
        private readonly FakeAiServiceControl _services = new();

        // What the in-flight probe reads; a test sets one to have that work running.
        private int _audioItems;
        private int _characterParagraphs;
        private bool _voiceBatch;
        private bool _discovery;
        private bool _assembly;

        public SetupCheckFullCheckTests()
        {
            Directory.CreateDirectory(_workspace);
            _config["Workspace:FolderPath"] = _workspace;
            foreach (var service in new DockerAiServiceRegistry().All)
                _services.ResolveByUrl[service.BaseUrl] = service;
        }

        public void Dispose()
        {
            _encode.Release.TrySetResult(new FfmpegProbeResult(true, "released by Dispose"));
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }

        // ── Fakes ────────────────────────────────────────────────────────────

        /// <summary>An encode that waits for the test to answer it, so a test can look at a run mid-step.</summary>
        private sealed class HeldEncode : IFfmpegEncodeCheck
        {
            public TaskCompletionSource<FfmpegProbeResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Calls;

            public Task<FfmpegProbeResult> EncodeToneAsync(string? ffmpegPath, CancellationToken ct = default)
            {
                Interlocked.Increment(ref Calls);
                Entered.TrySetResult();
                return Release.Task;
            }
        }

        private sealed class AnsweringFfmpeg : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffmpeg version 7.1"));

            public Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffprobe version 7.1"));
        }

        private sealed class NoDocker : IProcessRunner
        {
            public Task<(int ExitCode, string Output)> RunAsync(
                string fileName, string arguments, TimeSpan timeout, CancellationToken ct) =>
                throw new Win32Exception(2, "not found");
        }

        // ── Builders ─────────────────────────────────────────────────────────

        private SetupCheckRunner Runner(IFfmpegEncodeCheck? encode = null, IFfmpegProber? prober = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(_config).Build();
            return new(
                configuration,
                @"C:\app",
                Factory,
                prober ?? new AnsweringFfmpeg(),
                new NoDocker(),
                new WorkspaceStart(configuration),
                SetupCheckTestServices.Create(Factory, _services),
                SetupCheckTestServices.Full(
                    new InFlightWork(() => _audioItems, () => _characterParagraphs, () => _voiceBatch, () => _discovery, () => _assembly),
                    _watchdog, encode ?? _encode, _hub, _characterGate, _audioGate, _busy));
        }

        private static SetupCheckRow Row(SetupCheckReport report, string id) => report.Rows.Single(r => r.Id == id);

        private IReadOnlyList<SetupCheckMessage> Sent() =>
            [.. _hub.Method("setupCheck").Select(m => (SetupCheckMessage)m.Payload)];

        private Task SetLiveFfmpegPathAsync(string? path) =>
            new AudioProcessingSettingsService(Factory, new FfmpegProber(NullLogger<FfmpegProber>.Instance),
                NullLogger<AudioProcessingSettingsService>.Instance).SetFfmpegPathAsync(path);

        // ── Refusal ──────────────────────────────────────────────────────────

        public static TheoryData<string, string> InFlight => new()
        {
            { "audio", "the Audio queue holds 3 items — cancel it in Activity → Jobs" },
            { "character", "the Character queue holds 1 paragraph — cancel it in Activity → Jobs" },
            { "voiceBatch", "a voice batch is running — cancel it in Activity → Jobs" },
            { "discovery", "character discovery is running — wait for it to finish" },
            { "assembly", "audiobook assembly is running — cancel it in Activity → Jobs" },
        };

        [Theory]
        [MemberData(nameof(InFlight))]
        public async Task It_refuses_while_work_is_in_flight_saying_what_runs_and_how_to_stop_it(string kind, string says)
        {
            switch (kind)
            {
                case "audio": _audioItems = 3; break;
                case "character": _characterParagraphs = 1; break;
                case "voiceBatch": _voiceBatch = true; break;
                case "discovery": _discovery = true; break;
                case "assembly": _assembly = true; break;
            }
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var start = runner.StartFullCheck();

            Assert.Equal($"The Full check can't start while other work is running: {says}.", start.Refusal);
            Assert.False(start.Joined);
            Assert.True(start.Finished.IsCompleted);
            Assert.Null(runner.Report.FullRun);
            Assert.Null(runner.Report.FullCheckedAt);
            Assert.Equal(0, _encode.Calls);
            Assert.True(_characterGate.IsOpen);
            Assert.True(_audioGate.IsOpen);
            Assert.False(_busy.Running);
            Assert.Empty(Sent());
        }

        [Fact]
        public async Task A_refusal_names_every_kind_of_work_in_flight()
        {
            _audioItems = 1;
            _assembly = true;
            var runner = Runner();

            var start = runner.StartFullCheck();

            Assert.Equal(
                "The Full check can't start while other work is running: the Audio queue holds 1 item — cancel it in Activity → Jobs; audiobook assembly is running — cancel it in Activity → Jobs.",
                start.Refusal);
            await Task.CompletedTask;
        }

        // ── During the run ───────────────────────────────────────────────────

        [Fact]
        public async Task During_the_run_every_queue_waits_Preflight_is_refused_and_the_watchdog_holds_off_the_services_in_use()
        {
            // An active LLM config on llama: the Full check drives what active configs use.
            await new LlmSettingsService(Factory, NullLogger<LlmSettingsService>.Instance)
                .CreateConfigAsync(new LlmServerConfig { Name = "Qwen", BaseUrl = "http://localhost:8080", Model = "qwen-28b" });
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var start = runner.StartFullCheck();
            await _encode.Entered.Task.WaitAsync(Wait);

            Assert.Null(start.Refusal);
            Assert.False(_characterGate.IsOpen);
            Assert.False(_audioGate.IsOpen);
            Assert.Equal("Setup check running", _characterGate.CloseReason);
            Assert.Equal("Setup check running", _audioGate.CloseReason);
            Assert.True(_busy.Running);
            Assert.Equal(["llama"], _watchdog.Held);

            _encode.Release.SetResult(new FfmpegProbeResult(true, "Encoded a 1 s tone"));
            await start.Finished.WaitAsync(Wait);

            Assert.True(_characterGate.IsOpen);
            Assert.True(_audioGate.IsOpen);
            Assert.False(_busy.Running);
            Assert.Empty(_watchdog.Held);
        }

        [Fact]
        public async Task A_Quick_check_asked_for_during_the_run_answers_the_run_s_report()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            var start = runner.StartFullCheck();
            await _encode.Entered.Task.WaitAsync(Wait);

            var during = await runner.RunQuickCheckAsync(CancellationToken.None);

            Assert.Equal(SetupCheckResult.Checking, Row(during, SetupCheckIds.Ffmpeg).Result);
            Assert.NotNull(during.FullRun);
            _encode.Release.SetResult(new FfmpegProbeResult(true, "Encoded a 1 s tone"));
            await start.Finished.WaitAsync(Wait);
        }

        [Fact]
        public async Task Progress_goes_to_every_client_the_step_s_rows_read_Checking_and_done_carries_the_final_report()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var start = runner.StartFullCheck();
            await _encode.Entered.Task.WaitAsync(Wait);
            _encode.Release.SetResult(new FfmpegProbeResult(true, "Encoded a 1 s tone to a 9,000-byte .m4b"));
            var final = await start.Finished.WaitAsync(Wait);

            var sent = Sent();
            Assert.All(_hub.Method("setupCheck"), m => Assert.Equal(LiveGroups.Global, m.Target));
            var progress = sent.Last(m => m.Kind == "progress");
            Assert.Equal(new SetupFullRun(1, 1, SetupCheckLane.Local, "Encoding a 1 s tone to .m4b", Cancelling: false), progress.Report.FullRun);
            Assert.Equal(SetupCheckResult.Checking, Row(progress.Report, SetupCheckIds.Ffmpeg).Result);

            var done = sent[^1];
            Assert.Equal("done", done.Kind);
            Assert.Null(done.Report.FullRun);
            Assert.NotNull(done.Report.FullCheckedAt);
            var ffmpeg = Row(done.Report, SetupCheckIds.Ffmpeg);
            Assert.Equal(SetupCheckResult.Passed, ffmpeg.Result);
            Assert.Equal(SetupCheckTier.Full, ffmpeg.Tier);
            Assert.Equal("Encoded a 1 s tone to a 9,000-byte .m4b", ffmpeg.Found);
            Assert.Equal(done.Report.FullCheckedAt, final.FullCheckedAt);
        }

        // ── Joining and cancelling ───────────────────────────────────────────

        [Fact]
        public async Task A_second_request_joins_the_run_already_going()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            var first = runner.StartFullCheck();
            await _encode.Entered.Task.WaitAsync(Wait);
            // Work arriving after the start doesn't refuse a request that joins.
            _audioItems = 2;

            var second = runner.StartFullCheck();

            Assert.True(second.Joined);
            Assert.Null(second.Refusal);
            Assert.Same(first.Finished, second.Finished);
            Assert.Equal(new SetupFullRun(1, 1, SetupCheckLane.Local, "Encoding a 1 s tone to .m4b", false), second.Report.FullRun);
            _encode.Release.SetResult(new FfmpegProbeResult(true, "Encoded a 1 s tone"));
            await first.Finished.WaitAsync(Wait);
            Assert.Equal(1, _encode.Calls);
        }

        [Fact]
        public async Task Cancel_lets_the_current_step_finish_and_marks_it_cancelling()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            var start = runner.StartFullCheck();
            await _encode.Entered.Task.WaitAsync(Wait);

            var cancelling = runner.CancelFullCheck();

            Assert.True(cancelling?.FullRun?.Cancelling);
            Assert.Contains(Sent(), m => m.Kind == "progress" && m.Report.FullRun?.Cancelling == true);
            _encode.Release.SetResult(new FfmpegProbeResult(true, "Encoded a 1 s tone"));
            var final = await start.Finished.WaitAsync(Wait);
            Assert.Equal(SetupCheckResult.Passed, Row(final, SetupCheckIds.Ffmpeg).Result);
            Assert.True(_audioGate.IsOpen);
        }

        [Fact]
        public async Task Checks_a_cancelled_run_hadn_t_reached_read_Not_run()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            // Cancelled as the run takes its first hold, before its first step.
            _watchdog.OnHoldOff = () => runner.CancelFullCheck();

            var final = await runner.StartFullCheck().Finished.WaitAsync(Wait);

            var ffmpeg = Row(final, SetupCheckIds.Ffmpeg);
            Assert.Equal(SetupCheckResult.NotRun, ffmpeg.Result);
            Assert.Equal("Full check cancelled", ffmpeg.Found);
            Assert.Equal(0, _encode.Calls);
            Assert.Null(final.FullRun);
            Assert.Equal("done", Sent()[^1].Kind);
        }

        [Fact]
        public void Cancel_with_no_run_going_answers_null()
        {
            Assert.Null(Runner().CancelFullCheck());
        }

        // ── Results ──────────────────────────────────────────────────────────

        [Fact]
        public async Task A_later_Quick_check_keeps_the_Full_check_s_verdict_and_its_time()
        {
            var runner = Runner();
            await runner.RunQuickCheckAsync(CancellationToken.None);
            var start = runner.StartFullCheck();
            _encode.Release.SetResult(new FfmpegProbeResult(false, "No such filter: 'adynamicequalizer'"));
            var final = await start.Finished.WaitAsync(Wait);

            var after = await runner.RunQuickCheckAsync(CancellationToken.None);

            var ffmpeg = Row(after, SetupCheckIds.Ffmpeg);
            Assert.Equal(SetupCheckResult.Failed, ffmpeg.Result);
            Assert.Equal("No such filter: 'adynamicequalizer'", ffmpeg.Found);
            Assert.Equal(final.FullCheckedAt, after.FullCheckedAt);
        }

        [Fact]
        public async Task The_report_carries_an_estimate_for_the_confirm()
        {
            var report = await Runner().RunQuickCheckAsync(CancellationToken.None);

            Assert.True(report.FullCheckEstimateSeconds > 0);
        }

        // ── The ffmpeg step, for real ────────────────────────────────────────

        [Fact]
        public async Task The_ffmpeg_step_passes_when_the_live_ffmpeg_encodes_a_filtered_tone_to_m4b()
        {
            await SetLiveFfmpegPathAsync(TestFfmpeg.Require());
            var runner = Runner(new FfmpegEncodeCheck(NullLogger<FfmpegEncodeCheck>.Instance));
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var final = await runner.StartFullCheck().Finished.WaitAsync(Wait);

            var ffmpeg = Row(final, SetupCheckIds.Ffmpeg);
            Assert.Equal(SetupCheckResult.Passed, ffmpeg.Result);
            Assert.Matches(@"^Encoded a 1 s tone to a [\d,]+-byte \.m4b$", ffmpeg.Found);
        }

        [Fact]
        public async Task A_failed_encode_offers_the_appsettings_ffmpeg_when_that_one_encodes()
        {
            var ffmpeg = TestFfmpeg.Require();
            await SetLiveFfmpegPathAsync(TestFfmpeg.BogusPath());
            _config["Ffmpeg:Path"] = ffmpeg;
            var runner = Runner(new FfmpegEncodeCheck(NullLogger<FfmpegEncodeCheck>.Instance));
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var final = await runner.StartFullCheck().Finished.WaitAsync(Wait);

            var row = Row(final, SetupCheckIds.Ffmpeg);
            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal(ffmpeg, row.Fix.Copy);
            Assert.Equal("The appsettings path encodes: use it as the ffmpeg path.", row.Fix.Text);
        }

        [Fact]
        public async Task The_ffmpeg_step_fails_where_the_live_path_has_no_ffmpeg()
        {
            var bogus = TestFfmpeg.BogusPath();
            await SetLiveFfmpegPathAsync(bogus);
            var runner = Runner(new FfmpegEncodeCheck(NullLogger<FfmpegEncodeCheck>.Instance));
            await runner.RunQuickCheckAsync(CancellationToken.None);

            var final = await runner.StartFullCheck().Finished.WaitAsync(Wait);

            var ffmpeg = Row(final, SetupCheckIds.Ffmpeg);
            Assert.Equal(SetupCheckResult.Failed, ffmpeg.Result);
            Assert.Equal($"not found at {bogus}", ffmpeg.Found);
            Assert.Equal("settings/audio", ffmpeg.Fix.Link?.Path);
        }
    }
}
