using Microsoft.Extensions.Configuration;
using Read2Me.App.Configuration;
using Read2Me.Services;
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
    /// The host's one <see cref="ISetupCheckRunner"/>: the Quick check, composed with the
    /// <see cref="FullCheck"/> and the <see cref="SetupCheckLedger"/> both report into. The Quick check
    /// covers the Workspace, ffmpeg, the docker CLI, the active AI configs and the docker AI services;
    /// one runs at a time and each replaces the whole report but for the verdicts the last Full check
    /// left, which a Quick check overrides only by failing.
    /// </summary>
    public sealed class SetupCheckRunner : ISetupCheckRunner
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

        private readonly IConfiguration _configuration;
        private readonly string _contentRoot;
        private readonly IFfmpegProber _ffmpegProber;
        private readonly IProcessRunner _processes;
        private readonly WorkspaceStart _workspaceStart;
        private readonly SetupCheckServices _aiServices;
        private readonly SemaphoreSlim _quick = new(1, 1);
        private readonly SetupCheckLedger _ledger;
        private readonly FullCheck _full;

        public SetupCheckRunner(
            IConfiguration configuration,
            string contentRoot,
            IFfmpegProber ffmpegProber,
            IProcessRunner processes,
            WorkspaceStart workspaceStart,
            SetupCheckServices aiServices,
            FullCheckServices full,
            TimeProvider? clock = null)
        {
            _configuration = configuration;
            _contentRoot = contentRoot;
            _ffmpegProber = ffmpegProber;
            _processes = processes;
            _workspaceStart = workspaceStart;
            _aiServices = aiServices;
            clock ??= TimeProvider.System;
            var startedAt = clock.GetUtcNow();
            // The first-run and no-Workspace flags and the start time are this start's, stamped on as the report is read.
            _ledger = new SetupCheckLedger(full.Changes, clock, report => report with
            {
                FirstRun = _workspaceStart.CreatedDatabase,
                StartedAt = startedAt,
                NoWorkspace = !_workspaceStart.Usable,
            });
            _full = new FullCheck(new FullCheckDeps(_aiServices, full, _workspaceStart, _ledger));
        }

        public SetupCheckReport Report => _ledger.Report;

        public Task<FullCheckStart> StartFullCheckAsync() => _full.StartAsync(rerun: null);

        public Task<FullCheckStart> RerunCheckAsync(string checkId) => _full.StartAsync(checkId);

        public SetupCheckReport? CancelFullCheck() => _ledger.Cancel();

        public async Task<SetupCheckReport> RunQuickCheckAsync(CancellationToken ct)
        {
            // The run owns the rows until it ends.
            if (_ledger.Running)
                return Report;
            await _quick.WaitAsync(ct);
            try
            {
                if (!_workspaceStart.Usable)
                {
                    _ledger.QuickChecked(NoWorkspaceRows(), _ => null, estimateSeconds: null);
                    return Report;
                }

                // One read for every row, the plan, the re-run flags and the estimate.
                var configs = await ActiveAiConfigs.ReadAsync(_aiServices);
                var docker = await CheckDockerAsync(ct);
                var rows = new List<SetupCheckRow>
                {
                    CheckWorkspace(),
                    await CheckFfmpegAsync(ct),
                    docker.Row,
                };
                // One status read for every container row and the estimate; none without the docker CLI.
                var statuses = docker.OnPath
                    ? await configs.ReadStatusesAsync(_aiServices.Control, _aiServices.Registry, ct)
                    : new Dictionary<string, AiServiceStatus>();
                rows.AddRange(await new AiServicesCheck(_aiServices).RunAsync(configs, statuses, docker.OnPath, ct));
                var preview = _full.Preview(configs, statuses);
                _ledger.QuickChecked(rows, preview.Rerun, preview.EstimateSeconds);
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
        private IReadOnlyList<SetupCheckRow> NoWorkspaceRows()
        {
            var fix = new SetupFixHint("Fix the Workspace row first, then restart the app.", null, null);
            var rows = new List<SetupCheckRow>
            {
                WorkspaceRow(_workspaceStart.AtStart),
                SetupChecks.Ffmpeg.NotRun(NeedsWorkspace, fix),
                SetupChecks.Docker.NotRun(NeedsWorkspace, fix),
            };
            rows.AddRange(new AiServicesCheck(_aiServices).NotRun(NeedsWorkspace, fix));
            return rows;
        }

        /// <summary>A folder removed since start is created again, as start-up would have.</summary>
        private SetupCheckRow CheckWorkspace() => WorkspaceRow(_workspaceStart.Judge(_configuration[WorkspaceStart.Key]));

        private SetupCheckRow WorkspaceRow(WorkspaceVerdict verdict) => SetupChecks.Workspace.Row(
            verdict.Usable ? SetupCheckResult.Passed : SetupCheckResult.Failed, verdict.Found,
            verdict.Usable && _workspaceStart.CreatedFolder ? CreatedNote : null, WorkspaceFix());

        /// <summary>
        /// Names the key, the absolute path of appsettings.Local.json and the source whose value
        /// takes effect, so the user doesn't edit a file an environment variable or the command line overrides.
        /// </summary>
        private SetupFixHint WorkspaceFix()
        {
            var localPath = Path.Combine(_contentRoot, LocalSettings.FileName);
            var local = File.Exists(localPath)
                ? $"in {localPath}"
                : $"in {localPath} (it doesn't exist yet: copy it from appsettings.Local.example.json beside it)";
            var source = ConfigSources.Winning(_configuration, WorkspaceStart.Key);
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
            return new SetupFixHint(text, null, localPath);
        }


        // ── ffmpeg ───────────────────────────────────────────────────────────

        /// <summary>
        /// The live path is the one audio generation runs: the Settings value, else config's
        /// <c>Ffmpeg:Path</c>, else PATH. A saved path hides a different config one, so that earns
        /// a note, and the fix hint offers it when it would answer where the live one doesn't.
        /// </summary>
        private async Task<SetupCheckRow> CheckFfmpegAsync(CancellationToken ct)
        {
            var paths = await FfmpegPaths.ReadAsync(_aiServices.Scopes, ct);
            var (ok, found) = await ProbeFfmpegAsync(paths.Live, ct);
            SetupFixHint? fix = null;
            if (!ok && paths.Note is not null && (await ProbeFfmpegAsync(paths.Configured, ct)).Ok)
                fix = new SetupFixHint("The appsettings path answers: use it as the ffmpeg path.", SetupSettingsLink.AudioProcessing, paths.Configured);

            return SetupChecks.Ffmpeg.Row(ok ? SetupCheckResult.Passed : SetupCheckResult.Failed, found, paths.Note, fix)
                with { Expected = $"{SetupChecks.Ffmpeg.Expected} from {paths.Shown}" };
        }

        /// <summary>Both tools must answer; found is ffmpeg's banner, or which one failed and why.</summary>
        private async Task<(bool Ok, string Found)> ProbeFfmpegAsync(string? ffmpegPath, CancellationToken ct)
        {
            var ffmpeg = await _ffmpegProber.ProbeAsync(ffmpegPath, ct);
            if (!ffmpeg.Success)
                return (false, $"ffmpeg: {ffmpeg.Message}");
            var ffprobe = await _ffmpegProber.ProbeFfprobeAsync(ffmpegPath, ct);
            return ffprobe.Success ? (true, ffmpeg.Message) : (false, $"ffprobe: {ffprobe.Message}");
        }

        // ── docker CLI ───────────────────────────────────────────────────────

        /// <summary>The docker CLI row, and whether docker is on PATH for the container rows.</summary>
        private async Task<(SetupCheckRow Row, bool OnPath)> CheckDockerAsync(CancellationToken ct)
        {
            var run = await VersionAsync("docker", "--version", ct);
            return (SetupChecks.Docker.Row(
                run.Ok ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                run.Launched ? run.Banner : "docker isn't on PATH"), run.Launched);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary><c>Launched</c> false: the executable wasn't found or couldn't start.</summary>
        private readonly record struct VersionRun(bool Launched, bool Ok, string Banner);

        /// <summary>Runs <c>exe arguments</c> and reads the first line of what it printed.</summary>
        private async Task<VersionRun> VersionAsync(string exe, string arguments, CancellationToken ct)
        {
            try
            {
                var (exitCode, output) = await _processes.RunAsync(exe, arguments, ProcessTimeout, ct);
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
