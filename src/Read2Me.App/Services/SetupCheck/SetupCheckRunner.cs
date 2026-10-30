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
    /// time and each replaces the whole report but for the verdicts the last Full check left (see
    /// <c>SetupCheckRunner.Full.cs</c>), which a Quick check overrides only by failing.
    /// </summary>
    public sealed partial class SetupCheckRunner(
        IConfiguration configuration,
        string contentRoot,
        IDbContextFactory<Read2MeDbContext> dbFactory,
        IFfmpegProber ffmpegProber,
        IProcessRunner processes,
        WorkspaceStart workspaceStart,
        SetupCheckServices aiServices,
        FullCheckServices full,
        TimeProvider? clock = null) : ISetupCheckRunner
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private readonly SemaphoreSlim _quick = new(1, 1);
        private readonly DateTimeOffset _startedAt = (clock ?? TimeProvider.System).GetUtcNow();
        private volatile SetupCheckReport _report = SetupCheckReport.Empty;

        /// <summary>The first-run and no-Workspace flags and the start time are this start's, read as the report is.</summary>
        public SetupCheckReport Report => _report with
        {
            FirstRun = workspaceStart.CreatedDatabase,
            StartedAt = _startedAt,
            NoWorkspace = !workspaceStart.Usable,
        };

        public async Task<SetupCheckReport> RunQuickCheckAsync(CancellationToken ct)
        {
            // The run owns the rows until it ends.
            if (FullCheckRunning)
                return Report;
            await _quick.WaitAsync(ct);
            try
            {
                if (!workspaceStart.Usable)
                {
                    lock (_lock)
                        _report = NoWorkspaceReport();
                    return Report;
                }

                var docker = await CheckDockerAsync(ct);
                var rows = new List<SetupCheckRow>
                {
                    CheckWorkspace(),
                    await CheckFfmpegAsync(ct),
                    docker.Row,
                };
                rows.AddRange(await new AiServicesCheck(aiServices).RunAsync(docker.OnPath, ct));
                var context = await ReadFullContextAsync();
                var estimate = await EstimateSecondsAsync(context, docker.OnPath);
                lock (_lock)
                {
                    if (_run is null)
                        _report = _report with
                        {
                            Rows = WithRerun(WithFullVerdicts(rows), Plan(context)),
                            QuickCheckedAt = _clock.GetUtcNow(),
                            FullCheckEstimateSeconds = estimate,
                        };
                }
                return Report;
            }
            finally
            {
                _quick.Release();
            }
        }

        // ── Workspace ────────────────────────────────────────────────────────

        private const string CreatedNote = "The folder didn't exist, so the app created it.";
        private const string NeedsWorkspace = "needs the Workspace";

        /// <summary>The environment variable that sets the key: the key with each <c>:</c> as <c>__</c>.</summary>
        private static readonly string EnvVar = WorkspaceStart.Key.Replace(":", "__");

        /// <summary>
        /// With no usable Workspace at start, the Workspace row is the start's verdict and every
        /// other row is Not run: nothing reads app.db, and only a restart leaves the mode.
        /// </summary>
        private SetupCheckReport NoWorkspaceReport()
        {
            var fix = new SetupFixHint("Fix the Workspace row first, then restart the app.");
            var rows = new List<SetupCheckRow>
            {
                WorkspaceRow(workspaceStart.AtStart),
                new(SetupCheckIds.Ffmpeg, SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Quick,
                    SetupCheckResult.NotRun, "ffmpeg -version and ffprobe -version", NeedsWorkspace, null, fix, Managed: false),
                new(SetupCheckIds.Docker, SetupCheckGroup.WorkspaceAndTools, "docker CLI", SetupCheckTier.Quick,
                    SetupCheckResult.NotRun, "docker on PATH", NeedsWorkspace, null, fix, Managed: false),
            };
            rows.AddRange(new AiServicesCheck(aiServices).NotRun(NeedsWorkspace, fix));
            return _report with { Rows = rows, QuickCheckedAt = _clock.GetUtcNow() };
        }

        /// <summary>A folder removed since start is created again, as start-up would have.</summary>
        private SetupCheckRow CheckWorkspace() => WorkspaceRow(workspaceStart.Judge(configuration[WorkspaceStart.Key]));

        private SetupCheckRow WorkspaceRow(WorkspaceVerdict verdict) => new(
            SetupCheckIds.Workspace, SetupCheckGroup.WorkspaceAndTools, "Workspace", SetupCheckTier.Quick,
            verdict.Usable ? SetupCheckResult.Passed : SetupCheckResult.Failed,
            $"An absolute, writable folder ({WorkspaceStart.Key})", verdict.Found,
            verdict.Usable && workspaceStart.CreatedFolder ? CreatedNote : null,
            WorkspaceFix(), Managed: false);

        /// <summary>
        /// Names the key, the absolute path of appsettings.Local.json and the source whose value
        /// takes effect, so the user doesn't edit a file an environment variable or the command line overrides.
        /// </summary>
        private SetupFixHint WorkspaceFix()
        {
            var localPath = Path.Combine(contentRoot, LocalSettings.FileName);
            var local = File.Exists(localPath)
                ? $"in {localPath}"
                : $"in {localPath} (it doesn't exist yet: copy it from appsettings.Local.example.json beside it)";
            var source = ConfigSources.Winning(configuration, WorkspaceStart.Key);
            var text = source switch
            {
                { Kind: ConfigSourceKind.EnvironmentVariable } =>
                    $"{WorkspaceStart.Key} comes from the environment variable {EnvVar}, which overrides {LocalSettings.FileName}: set it to an absolute, writable folder, or remove it and set the key {local}, then restart the app.",
                { Kind: ConfigSourceKind.CommandLine } =>
                    $"{WorkspaceStart.Key} comes from the command line (--{WorkspaceStart.Key}), which overrides {LocalSettings.FileName}: pass an absolute, writable folder, or drop it and set the key {local}, then restart the app.",
                // A blank in a settings file (appsettings.json ships one) is a placeholder, not a choice.
                { Value: var value } when string.IsNullOrWhiteSpace(value) =>
                    $"Set {WorkspaceStart.Key} to an absolute, writable folder {local}, then restart the app.",
                { Kind: ConfigSourceKind.Other, Origin: var provider } =>
                    $"{WorkspaceStart.Key} comes from {provider}, which overrides {LocalSettings.FileName}: change it there, then restart the app.",
                { Kind: ConfigSourceKind.File, Origin: var file }
                    when !string.Equals(file, LocalSettings.FileName, StringComparison.OrdinalIgnoreCase) =>
                    $"{WorkspaceStart.Key} comes from {file}: set it to an absolute, writable folder {local}, which overrides it, then restart the app.",
                _ => $"Set {WorkspaceStart.Key} to an absolute, writable folder {local}, then restart the app.",
            };
            return new SetupFixHint(text, Copy: localPath);
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
            var paths = await ReadFfmpegPathsAsync(ct);
            var (ok, found) = await ProbeFfmpegAsync(paths.Live, ct);
            var fix = FfmpegFix;
            if (!ok && paths.Note is not null && (await ProbeFfmpegAsync(paths.Seeded, ct)).Ok)
                fix = new SetupFixHint("The appsettings path answers: use it as the ffmpeg path.", AudioProcessing, paths.Seeded);

            // A Full-tier row: the Quick check judges -version, the Full check the encode.
            return new SetupCheckRow(
                SetupCheckIds.Ffmpeg, SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Full,
                ok ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                $"ffmpeg -version and ffprobe -version from {paths.Shown}", found, paths.Note, fix, Managed: false);
        }

        private static readonly SetupFixHint FfmpegFix =
            new("Set the ffmpeg path to an ffmpeg with ffprobe beside it.", AudioProcessing);

        /// <summary>
        /// <see cref="Live"/>: the Settings value (null: PATH). <see cref="Seeded"/>: config's
        /// <c>Ffmpeg:Path</c>. <see cref="Note"/>: set when they differ.
        /// </summary>
        private sealed record FfmpegPaths(string? Live, string? Seeded, string Shown, string? Note);

        private async Task<FfmpegPaths> ReadFfmpegPathsAsync(CancellationToken ct)
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
            return new FfmpegPaths(live, seeded, shown, note);
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
