using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Services.SetupCheck;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Health;
using Read2Me.Tests.Infrastructure;
using Read2Me.Tests.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The Quick check's Workspace, ffmpeg and docker CLI rows, through <see cref="ISetupCheckRunner"/>.
    /// ffmpeg runs for real (skipping without one); docker is faked so a test can take it off PATH.
    /// </summary>
    public class SetupCheckRunnerTests : AppDbTestBase, IDisposable
    {
        private const string ContentRoot = @"C:\app";
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), "r2me-setup-check", Guid.NewGuid().ToString("N"));
        private readonly FakeDocker _processes = new();
        private readonly Dictionary<string, string?> _config = new();

        public SetupCheckRunnerTests()
        {
            Directory.CreateDirectory(_workspace);
            _config["Workspace:FolderPath"] = _workspace;
        }

        public void Dispose()
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }

        /// <summary>Runs every process for real except <c>docker</c>, which answers as configured.</summary>
        private sealed class FakeDocker : IProcessRunner
        {
            private readonly ProcessRunner _real = new();
            public (int ExitCode, string Output)? Docker = (0, "Docker version 28.4.0, build d8eb465");

            public Task<(int ExitCode, string Output)> RunAsync(
                string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
            {
                if (fileName != "docker")
                    return _real.RunAsync(fileName, arguments, timeout, ct);
                if (Docker is not { } answer)
                    throw new Win32Exception(2, "The system cannot find the file specified.");
                return Task.FromResult(answer);
            }
        }

        private SetupCheckRunner Runner(WorkspaceStart? start = null, IFfmpegProber? prober = null) => new(
            new ConfigurationBuilder().AddInMemoryCollection(_config).Build(),
            ContentRoot,
            Factory,
            prober ?? new FfmpegProber(NullLogger<FfmpegProber>.Instance),
            _processes,
            start ?? new WorkspaceStart(),
            SetupCheckTestServices.Create(Factory));

        private async Task<SetupCheckRow> RowAsync(string id, WorkspaceStart? start = null) =>
            (await Runner(start).RunQuickCheckAsync(CancellationToken.None)).Rows.Single(r => r.Id == id);

        private Task SetLiveFfmpegPathAsync(string? path) =>
            new AudioProcessingSettingsService(Factory, new FfmpegProber(NullLogger<FfmpegProber>.Instance),
                NullLogger<AudioProcessingSettingsService>.Instance).SetFfmpegPathAsync(path);

        [Fact]
        public async Task Docker_not_on_PATH_fails_with_an_install_hint()
        {
            _processes.Docker = null;

            var row = await RowAsync(SetupCheckIds.Docker);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal(SetupCheckGroup.WorkspaceAndTools, row.Group);
            Assert.Equal("docker on PATH", row.Expected);
            Assert.Equal("docker isn't on PATH", row.Found);
            Assert.Contains("Docker Desktop", row.Fix.Text);
        }

        [Fact]
        public async Task Docker_on_PATH_passes_with_its_version()
        {
            var row = await RowAsync(SetupCheckIds.Docker);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("Docker version 28.4.0, build d8eb465", row.Found);
        }

        [Fact]
        public async Task The_rows_come_in_table_order_and_the_check_time_is_kept()
        {
            var runner = Runner();
            Assert.Null(runner.Report.QuickCheckedAt);

            var report = await runner.RunQuickCheckAsync(CancellationToken.None);

            Assert.Equal([SetupCheckIds.Workspace, SetupCheckIds.Ffmpeg, SetupCheckIds.Docker], report.Rows.Take(3).Select(r => r.Id));
            Assert.All(report.Rows, r => Assert.Equal(SetupCheckTier.Quick, r.Tier));
            Assert.NotNull(report.QuickCheckedAt);
            Assert.Null(report.FullCheckedAt);
            Assert.Same(report, runner.Report);
        }

        // ── Workspace ────────────────────────────────────────────────────────

        [Fact]
        public async Task A_usable_workspace_passes_with_no_note()
        {
            var row = await RowAsync(SetupCheckIds.Workspace);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal(_workspace, row.Found);
            Assert.Null(row.Note);
        }

        [Fact]
        public async Task A_workspace_this_start_created_passes_with_a_note()
        {
            Directory.Delete(_workspace);
            var start = new WorkspaceStart();
            start.EnsureFolder(_workspace);

            var row = await RowAsync(SetupCheckIds.Workspace, start);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal("The folder didn't exist, so the app created it.", row.Note);
        }

        [Fact]
        public async Task A_workspace_folder_gone_since_start_is_created_again_with_the_note()
        {
            Directory.Delete(_workspace);

            var row = await RowAsync(SetupCheckIds.Workspace);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.True(Directory.Exists(_workspace));
            Assert.Equal("The folder didn't exist, so the app created it.", row.Note);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task An_unset_workspace_fails_naming_the_key_and_the_local_settings_file(string folderPath)
        {
            _config["Workspace:FolderPath"] = folderPath;

            var row = await RowAsync(SetupCheckIds.Workspace);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("Workspace:FolderPath isn't set", row.Found);
            var localSettings = Path.Combine(ContentRoot, "appsettings.Local.json");
            Assert.Contains("Workspace:FolderPath", row.Fix.Text);
            Assert.Contains("appsettings.Local.example.json", row.Fix.Text);
            Assert.Contains("restart the app", row.Fix.Text);
            Assert.Equal(localSettings, row.Fix.Copy);
            Assert.Null(row.Fix.Link);
        }

        [Fact]
        public async Task A_relative_workspace_fails()
        {
            _config["Workspace:FolderPath"] = "ws";

            var row = await RowAsync(SetupCheckIds.Workspace);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal("ws is a relative path", row.Found);
        }

        [Fact]
        public async Task A_workspace_that_is_a_file_fails()
        {
            var file = Path.Combine(_workspace, "app.txt");
            await File.WriteAllTextAsync(file, "not a folder");
            _config["Workspace:FolderPath"] = file;

            var row = await RowAsync(SetupCheckIds.Workspace);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal($"{file} is a file", row.Found);
        }

        // ── ffmpeg ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Ffmpeg_at_the_live_path_passes_when_ffmpeg_and_ffprobe_answer()
        {
            var ffmpeg = TestFfmpeg.Require();
            await SetLiveFfmpegPathAsync(ffmpeg);

            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal($"ffmpeg -version and ffprobe -version from {ffmpeg}", row.Expected);
            Assert.StartsWith("ffmpeg version", row.Found);
            Assert.Null(row.Note);
        }

        [Fact]
        public async Task A_blank_live_path_checks_ffmpeg_on_PATH()
        {
            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Equal("ffmpeg -version and ffprobe -version from PATH", row.Expected);
        }

        [Fact]
        public async Task Ffmpeg_that_does_not_answer_fails_with_a_link_to_audio_processing()
        {
            var bogus = TestFfmpeg.BogusPath();
            await SetLiveFfmpegPathAsync(bogus);

            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal($"ffmpeg: not found at {bogus}", row.Found);
            Assert.Equal(new SetupSettingsLink("settings/audio", "Settings → Audio processing"), row.Fix.Link);
            Assert.Null(row.Fix.Copy);
        }

        [Fact]
        public async Task An_appsettings_path_that_differs_from_the_live_one_adds_a_note()
        {
            var ffmpeg = TestFfmpeg.Require();
            await SetLiveFfmpegPathAsync(ffmpeg);
            _config["Ffmpeg:Path"] = @"D:\elsewhere\ffmpeg.exe";

            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Equal(SetupCheckResult.Passed, row.Result);
            Assert.Equal(
                $@"appsettings says D:\elsewhere\ffmpeg.exe; the app uses {ffmpeg} — change it in Settings → Audio processing",
                row.Note);
        }

        [Fact]
        public async Task An_appsettings_path_equal_to_the_live_one_adds_no_note()
        {
            var ffmpeg = TestFfmpeg.Require();
            await SetLiveFfmpegPathAsync(ffmpeg);
            _config["Ffmpeg:Path"] = $"  {ffmpeg} ";

            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Null(row.Note);
        }

        [Fact]
        public async Task A_failing_live_path_offers_the_appsettings_path_when_that_one_answers()
        {
            var ffmpeg = TestFfmpeg.Require();
            var bogus = TestFfmpeg.BogusPath();
            await SetLiveFfmpegPathAsync(bogus);
            _config["Ffmpeg:Path"] = ffmpeg;

            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal($"appsettings says {ffmpeg}; the app uses {bogus} — change it in Settings → Audio processing", row.Note);
            Assert.Contains("appsettings", row.Fix.Text);
            Assert.Equal(ffmpeg, row.Fix.Copy);
            Assert.Equal("settings/audio", row.Fix.Link?.Path);
        }

        /// <summary>ffmpeg answers but the ffprobe beside it doesn't.</summary>
        private sealed class NoFfprobe : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ffmpeg version 7.1"));

            public Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(false, @"not found at D:\ffmpeg\bin\ffprobe.exe"));
        }

        [Fact]
        public async Task Ffmpeg_without_ffprobe_beside_it_fails_naming_ffprobe()
        {
            var row = (await Runner(prober: new NoFfprobe()).RunQuickCheckAsync(CancellationToken.None))
                .Rows.Single(r => r.Id == SetupCheckIds.Ffmpeg);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Equal(@"ffprobe: not found at D:\ffmpeg\bin\ffprobe.exe", row.Found);
        }

        [Fact]
        public async Task A_failing_live_path_offers_nothing_when_the_appsettings_path_fails_too()
        {
            await SetLiveFfmpegPathAsync(TestFfmpeg.BogusPath());
            _config["Ffmpeg:Path"] = TestFfmpeg.BogusPath();

            var row = await RowAsync(SetupCheckIds.Ffmpeg);

            Assert.Equal(SetupCheckResult.Failed, row.Result);
            Assert.Null(row.Fix.Copy);
        }
    }
}
