using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Read2Me.App.Configuration;
using Read2Me.AppData;
using Read2Me.Services.Audio;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>The ids of the checks, stable on the wire.</summary>
    public static class SetupCheckIds
    {
        public const string Workspace = "workspace";
        public const string Ffmpeg = "ffmpeg";
        public const string Docker = "docker";
        public const string Llm = "llm";
        public const string AttributionChain = "attribution-chain";
        public const string ParagraphTts = "paragraph-tts";
        public const string VoiceDesign = "voice-design";
        public const string Transcription = "transcription";
        public const string Similarity = "similarity";

        /// <summary>A Docker services row, by the container's compose service name.</summary>
        public static string Container(string service) => $"container-{service}";
    }

    /// <summary>
    /// The host's one <see cref="ISetupCheckRunner"/>. The Quick check covers the Workspace, ffmpeg,
    /// the docker CLI, the active AI configs and the docker AI services; one Quick check runs at a
    /// time and each replaces the whole report.
    /// </summary>
    public sealed class SetupCheckRunner(
        IConfiguration configuration,
        string contentRoot,
        IDbContextFactory<Read2MeDbContext> dbFactory,
        IFfmpegProber ffmpegProber,
        IProcessRunner processes,
        WorkspaceStart workspaceStart,
        SetupCheckServices aiServices,
        TimeProvider? clock = null) : ISetupCheckRunner
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private readonly SemaphoreSlim _quick = new(1, 1);
        private volatile SetupCheckReport _report = SetupCheckReport.Empty;

        public SetupCheckReport Report => _report;

        public async Task<SetupCheckReport> RunQuickCheckAsync(CancellationToken ct)
        {
            await _quick.WaitAsync(ct);
            try
            {
                var docker = await CheckDockerAsync(ct);
                var rows = new List<SetupCheckRow>
                {
                    CheckWorkspace(),
                    await CheckFfmpegAsync(ct),
                    docker.Row,
                };
                rows.AddRange(await new AiServicesCheck(aiServices).RunAsync(docker.OnPath, ct));
                _report = _report with { Rows = rows, QuickCheckedAt = _clock.GetUtcNow() };
                return _report;
            }
            finally
            {
                _quick.Release();
            }
        }

        // ── Workspace ────────────────────────────────────────────────────────

        private const string WorkspaceKey = "Workspace:FolderPath";
        private const string CreatedNote = "The folder didn't exist, so the app created it.";

        private SetupCheckRow CheckWorkspace()
        {
            var localSettings = Path.Combine(contentRoot, LocalSettings.FileName);
            var fix = new SetupFixHint(
                $"Set {WorkspaceKey} to an absolute folder in {LocalSettings.FileName} (copy it from appsettings.Local.example.json if it doesn't exist), then restart the app.",
                Copy: localSettings);

            SetupCheckRow Row(SetupCheckResult result, string found, string? note = null) => new(
                SetupCheckIds.Workspace, SetupCheckGroup.WorkspaceAndTools, "Workspace", SetupCheckTier.Quick, result,
                $"An absolute, writable folder ({WorkspaceKey})", found, note, fix, Managed: false);

            // Untrimmed: the host binds and uses the value as written, so the check judges that same path.
            var path = configuration[WorkspaceKey];
            if (string.IsNullOrWhiteSpace(path))
                return Row(SetupCheckResult.Failed, $"{WorkspaceKey} isn't set");
            if (!Path.IsPathFullyQualified(path))
                return Row(SetupCheckResult.Failed, $"{path} is a relative path");
            if (File.Exists(path))
                return Row(SetupCheckResult.Failed, $"{path} is a file");

            try
            {
                // A folder removed since start is created again, as start-up would have.
                workspaceStart.EnsureFolder(path);
            }
            catch (Exception ex)
            {
                return Row(SetupCheckResult.Failed, $"{path} can't be created: {ex.Message}");
            }

            try
            {
                var probe = Path.Combine(path, $".setup-check-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                return Row(SetupCheckResult.Failed, $"{path} isn't writable: {ex.Message}");
            }

            return Row(SetupCheckResult.Passed, path, workspaceStart.CreatedFolder ? CreatedNote : null);
        }

        // ── ffmpeg ───────────────────────────────────────────────────────────

        private static readonly SetupSettingsLink AudioProcessing = new("settings/audio", "Settings → Audio processing");

        /// <summary>
        /// The live path is the Settings value (blank: PATH), what audio generation runs; config's
        /// <c>Ffmpeg:Path</c> only seeds a new database, so a different one earns a note, and the
        /// fix hint offers it when it would answer where the live one doesn't.
        /// </summary>
        private async Task<SetupCheckRow> CheckFfmpegAsync(CancellationToken ct)
        {
            string? live;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                live = Read2Me.Services.AudioProcessingSettingsService.NormalizeFfmpegPath(
                    (await db.Settings.SingleOrDefaultAsync(ct))?.FfmpegPath);
            var seeded = Read2Me.Services.AudioProcessingSettingsService.NormalizeFfmpegPath(configuration["Ffmpeg:Path"]);
            var shown = live ?? "PATH";

            var note = seeded is not null && !string.Equals(seeded, live, StringComparison.OrdinalIgnoreCase)
                ? $"appsettings says {seeded}; the app uses {shown} — change it in {AudioProcessing.Label}"
                : null;

            var (ok, found) = await ProbeFfmpegAsync(live, ct);
            var fix = new SetupFixHint("Set the ffmpeg path to an ffmpeg with ffprobe beside it.", AudioProcessing);
            if (!ok && note is not null && (await ProbeFfmpegAsync(seeded, ct)).Ok)
                fix = new SetupFixHint("The appsettings path answers: use it as the ffmpeg path.", AudioProcessing, seeded);

            return new SetupCheckRow(
                SetupCheckIds.Ffmpeg, SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Quick,
                ok ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                $"ffmpeg -version and ffprobe -version from {shown}", found, note, fix, Managed: false);
        }

        /// <summary>Both tools must answer; found is ffmpeg's banner, or which one failed and why.</summary>
        private async Task<(bool Ok, string Found)> ProbeFfmpegAsync(string? ffmpegPath, CancellationToken ct)
        {
            var ffmpeg = await ffmpegProber.ProbeAsync(ffmpegPath, ct);
            if (!ffmpeg.Success)
                return (false, $"ffmpeg: {ffmpeg.Message}");
            var ffprobe = await ffmpegProber.ProbeFfprobeAsync(ffmpegPath, ct);
            return ffprobe.Success ? (true, ffmpeg.Message) : (false, $"ffprobe: {ffprobe.Message}");
        }

        // ── docker CLI ───────────────────────────────────────────────────────

        /// <summary>The docker CLI row, and whether docker is on PATH for the container rows.</summary>
        private async Task<(SetupCheckRow Row, bool OnPath)> CheckDockerAsync(CancellationToken ct)
        {
            var fix = new SetupFixHint(
                "Install Docker Desktop so docker is on PATH, then restart the app.", Copy: "docker --version");
            var run = await VersionAsync("docker", "--version", ct);
            return (new SetupCheckRow(
                SetupCheckIds.Docker, SetupCheckGroup.WorkspaceAndTools, "docker CLI", SetupCheckTier.Quick,
                run.Ok ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                "docker on PATH",
                run.Launched ? run.Banner : "docker isn't on PATH",
                null, fix, Managed: false), run.Launched);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary><c>Launched</c> false: the executable wasn't found or couldn't start.</summary>
        private readonly record struct VersionRun(bool Launched, bool Ok, string Banner);

        /// <summary>Runs <c>exe arguments</c> and reads the first line of what it printed.</summary>
        private async Task<VersionRun> VersionAsync(string exe, string arguments, CancellationToken ct)
        {
            try
            {
                var (exitCode, output) = await processes.RunAsync(exe, arguments, ProcessTimeout, ct);
                var banner = FirstLine(output);
                return exitCode == 0
                    ? new VersionRun(true, true, banner)
                    : new VersionRun(true, false, banner.Length > 0 ? banner : $"exited with code {exitCode}");
            }
            catch (TimeoutException)
            {
                return new VersionRun(true, false, $"didn't answer within {ProcessTimeout.TotalSeconds:0} s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new VersionRun(false, false, ex.Message);
            }
        }

        private static string FirstLine(string text)
        {
            var nl = text.IndexOfAny(['\r', '\n']);
            return (nl < 0 ? text : text[..nl]).Trim();
        }
    }
}
