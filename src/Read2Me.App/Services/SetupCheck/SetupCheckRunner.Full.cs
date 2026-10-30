using Read2Me.App.Live;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Full check: one run per host, in the background, as a list of steps. While it runs every
    /// queue gate is closed and Preflight refuses new AI tasks (both with "Setup check running"),
    /// and the watchdog holds off the services the steps drive. Each step marks its rows
    /// Checking, runs on its own (a throw fails only its rows) and replaces them; every change goes
    /// to every client as a <c>setupCheck</c> message. A cancel lets the current step finish; the
    /// steps after it read Not run. Results live in memory only.
    /// </summary>
    public sealed partial class SetupCheckRunner
    {
        private const string Cancelled = "Full check cancelled";

        private readonly object _lock = new();
        // Guarded by _lock.
        private FullRun? _run;
        private readonly Dictionary<string, SetupCheckRow> _fullVerdicts = new(StringComparer.Ordinal);

        /// <summary>
        /// The run going; its cancel stops it after the current step. <see cref="Rerun"/>: the one
        /// check it re-runs, null for a whole Full check.
        /// </summary>
        private sealed class FullRun(string? rerun)
        {
            public string? Rerun { get; } = rerun;
            public CancellationTokenSource Cancel { get; } = new();
            public TaskCompletionSource<SetupCheckReport> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// One Full check step: where it runs, what the progress strip says, the rows it judges, the
        /// managed services it drives (held off the watchdog for the run), a rough duration for the
        /// confirm's estimate, and the work, which answers those rows.
        /// </summary>
        private sealed record FullStep(
            SetupCheckLane Lane,
            string Label,
            IReadOnlyList<string> RowIds,
            IReadOnlyList<string> Services,
            int EstimateSeconds,
            Func<Task<IReadOnlyList<SetupCheckRow>>> RunAsync);

        private bool FullCheckRunning
        {
            get { lock (_lock) return _run is not null; }
        }

        /// <summary>
        /// The steps, in run order: local first, then CPU services (whisper, then similarity), then GPU
        /// services one at a time. A check with no active config gets no step: its row stays Not used.
        /// </summary>
        private IReadOnlyList<FullStep> Plan(FullContext context)
        {
            var steps = new List<FullStep>
            {
                new(SetupCheckLane.Local, "Encoding a 1 s tone to .m4b", [SetupCheckIds.Ffmpeg], [], 3, FullFfmpegAsync),
            };
            steps.AddRange(CpuSteps(context));
            steps.AddRange(GpuSteps(context));
            return steps;
        }

        /// <summary>Serializes starts, so the configs one reads are the ones its run uses.</summary>
        private readonly SemaphoreSlim _starting = new(1, 1);

        public Task<FullCheckStart> StartFullCheckAsync() => StartRunAsync(rerun: null);

        public Task<FullCheckStart> RerunCheckAsync(string checkId) => StartRunAsync(checkId);

        /// <summary>
        /// Starts a whole Full check (<paramref name="rerun"/> null), which joins a whole one going, or
        /// a re-run of one check: only the steps that judge that row. Anything else going refuses it:
        /// a whole check can't join a re-run, which checks one row.
        /// </summary>
        private async Task<FullCheckStart> StartRunAsync(string? rerun)
        {
            await _starting.WaitAsync();
            try
            {
                lock (_lock)
                {
                    if (rerun is not null && _report.QuickCheckedAt is null)
                        return Refused("The start-up Quick check hasn't ended: re-run the check once it has.");
                    if (rerun is not null && RowOrNull(rerun) is not { Tier: SetupCheckTier.Full })
                        return Refused($"There's no Full-tier check \"{rerun}\" to re-run.", unknownCheck: true);
                    if (_run is { } going)
                        return (going.Rerun, rerun) switch
                        {
                            (null, null) => new FullCheckStart(Report, Joined: true, Refusal: null, going.Finished.Task),
                            (null, _) => Refused("A Full check is running: re-run the check once it ends."),
                            (_, null) => Refused("A check is re-running: run the Full check once it ends."),
                            _ => Refused("A check is re-running: re-run this one once it ends."),
                        };
                    if (FullRefusal() is { } refusal)
                        return Refused(refusal);
                }

                var context = await ReadFullContextAsync();

                FullRun run;
                IReadOnlyList<FullStep> steps;
                SetupCheckReport started;
                lock (_lock)
                {
                    // Work may have arrived while the configs were read.
                    if (FullRefusal() is { } refusal)
                        return Refused(refusal);
                    steps = rerun is null ? Plan(context) : StepsFor(Plan(context), rerun);
                    if (steps.Count == 0)
                        return Refused($"{RowOrNull(rerun!)?.Name ?? rerun} has nothing for the Full check to send: it has no active config.");
                    // The web app confirms a GPU re-run on the report's rerun.gpu, which the last Quick
                    // check set: a config moved onto a GPU container since must not skip that confirm.
                    if (rerun is not null && RowOrNull(rerun) is { Rerun: not { Gpu: true } } stale && DrivesGpu(steps))
                        return Refused($"{stale.Name} now drives a GPU container: run the Quick check, then re-run it.");

                    context.Before = _report.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
                    run = _run = new FullRun(rerun);
                    full.Busy.Set(true);
                    full.CharacterGate.Close(GateOwners.SetupCheck, SetupCheckBusy.Reason);
                    full.AudioGate.Close(GateOwners.SetupCheck, SetupCheckBusy.Reason);
                    _report = _report with
                    {
                        FullRun = new SetupFullRun(1, steps.Count, steps[0].Lane, steps[0].Label, Cancelling: false),
                        PutBack = null,
                    };
                    started = Report;
                    Publish("progress");
                }

                _ = Task.Run(() => RunFullAsync(run, steps, context));
                return new FullCheckStart(started, Joined: false, Refusal: null, run.Finished.Task);
            }
            finally
            {
                _starting.Release();
            }
        }

        // Caller holds _lock.
        private FullCheckStart Refused(string refusal, bool unknownCheck = false) =>
            new(Report, Joined: false, refusal, Task.FromResult(Report), unknownCheck);

        /// <summary>
        /// The steps a re-run of one row takes, in run order: those that judge it, and before them
        /// those that feed it. Similarity scores the transcript transcription gives, so re-running it
        /// transcribes first (when transcription has a config) rather than scoring the clip's text
        /// against itself.
        /// </summary>
        private static IReadOnlyList<FullStep> StepsFor(IReadOnlyList<FullStep> plan, string rowId)
        {
            string[] rows = rowId == SetupCheckIds.Similarity ? [rowId, SetupCheckIds.Transcription] : [rowId];
            return [.. plan.Where(s => s.RowIds.Any(rows.Contains))];
        }

        private bool DrivesGpu(IReadOnlyList<FullStep> steps)
        {
            var gpu = aiServices.Registry.All.Where(s => s.UsesGpu).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return steps.Any(s => s.Services.Any(gpu.Contains));
        }

        /// <summary>
        /// Each Full-tier row with what re-running it takes: null when no step judges it (no active
        /// config), and whether a step drives a GPU container, which the web app confirms first.
        /// </summary>
        private IReadOnlyList<SetupCheckRow> WithRerun(IReadOnlyList<SetupCheckRow> rows, IReadOnlyList<FullStep> plan) =>
            [.. rows.Select(row => row with
            {
                Rerun = row.Tier == SetupCheckTier.Full && StepsFor(plan, row.Id) is { Count: > 0 } steps
                    ? new SetupRerun(Gpu: DrivesGpu(steps))
                    : null,
            })];

        // Caller holds _lock.
        private string? FullRefusal() => workspaceStart.Usable
            ? full.Work.Refusal()
            : "The Full check needs the Workspace: fix the Workspace row, then restart the app.";

        public SetupCheckReport? CancelFullCheck()
        {
            lock (_lock)
            {
                if (_run is null)
                    return null;
                _run.Cancel.Cancel();
                if (_report.FullRun is { } going)
                    _report = _report with { FullRun = going with { Cancelling = true } };
                Publish("progress");
                return Report;
            }
        }

        private async Task RunFullAsync(FullRun run, IReadOnlyList<FullStep> steps, FullContext context)
        {
            var next = 0;
            string? putBack = null;
            try
            {
                // Held for the whole run, putting back included: a service one step stops for another
                // must not trip the watchdog.
                using var hold = full.Watchdog.HoldOff(
                    [.. steps.SelectMany(s => s.Services).Distinct(StringComparer.OrdinalIgnoreCase)]);
                try
                {
                    for (; next < steps.Count && !run.Cancel.IsCancellationRequested; next++)
                        await RunStepAsync(steps[next], next + 1, steps.Count, run.Cancel.Token);
                }
                finally
                {
                    putBack = await PutBackAsync(context);
                }
            }
            finally
            {
                lock (_lock)
                {
                    // Rows a step that ran already judged (a container or chain row several steps share) keep that verdict.
                    var judged = steps.Take(next).SelectMany(s => s.RowIds).ToHashSet(StringComparer.Ordinal);
                    SetRows([.. steps.Skip(next).SelectMany(s => s.RowIds).Where(id => !judged.Contains(id))],
                        row => row with { Result = SetupCheckResult.NotRun, Found = Cancelled, Note = null });
                    // A re-run judges one check: the time stays the last whole Full check's.
                    _report = _report with
                    {
                        FullRun = null,
                        FullCheckedAt = run.Rerun is null ? _clock.GetUtcNow() : _report.FullCheckedAt,
                        PutBack = putBack,
                    };
                    _run = null;
                    full.CharacterGate.Open(GateOwners.SetupCheck);
                    full.AudioGate.Open(GateOwners.SetupCheck);
                    full.Busy.Set(false);
                    Publish("done");
                    run.Finished.TrySetResult(Report);
                }
                run.Cancel.Dispose();
            }
        }

        /// <summary>One step: its rows read Checking, then its verdicts replace them.</summary>
        private async Task RunStepAsync(FullStep step, int number, int total, CancellationToken cancel)
        {
            lock (_lock)
            {
                // A cancel that lands after the loop's check still shows; the step still runs.
                _report = _report with
                {
                    FullRun = new SetupFullRun(number, total, step.Lane, step.Label, cancel.IsCancellationRequested),
                };
                SetRows(step.RowIds, row => row with { Result = SetupCheckResult.Checking });
                Publish("progress");
            }

            IReadOnlyList<SetupCheckRow> rows;
            try
            {
                rows = await step.RunAsync();
            }
            catch (Exception ex)
            {
                // Each step stands alone: a throw fails its own rows and the run goes on.
                lock (_lock)
                    rows = [.. step.RowIds.Select(RowOrNull).OfType<SetupCheckRow>()
                        .Select(row => row with { Result = SetupCheckResult.Failed, Found = ex.Message })];
            }

            lock (_lock)
            {
                foreach (var row in rows)
                {
                    _fullVerdicts[row.Id] = row;
                    SetRows([row.Id], _ => row);
                }
            }
        }

        /// <summary>
        /// A Quick check's rows with the last Full check's verdicts laid over them. A Full pass is the
        /// stronger proof, so it stands unless the Quick check now fails the row. A Full failure the
        /// Quick check no longer finds is dropped: the fix may have worked, and the next Full check proves it.
        /// Caller holds _lock.
        /// </summary>
        private IReadOnlyList<SetupCheckRow> WithFullVerdicts(IReadOnlyList<SetupCheckRow> rows)
        {
            var merged = new List<SetupCheckRow>(rows.Count);
            foreach (var row in rows)
            {
                if (row.Result == SetupCheckResult.Failed || !_fullVerdicts.TryGetValue(row.Id, out var verdict))
                    merged.Add(row);
                else if (verdict.Result != SetupCheckResult.Failed)
                    merged.Add(verdict);
                else
                {
                    _fullVerdicts.Remove(row.Id);
                    merged.Add(row);
                }
            }
            return merged;
        }

        // Caller holds _lock.
        private SetupCheckRow? RowOrNull(string id) => _report.Rows.FirstOrDefault(r => r.Id == id);

        // Caller holds _lock.
        private void SetRows(IReadOnlyList<string> ids, Func<SetupCheckRow, SetupCheckRow> change) =>
            _report = _report with { Rows = [.. _report.Rows.Select(r => ids.Contains(r.Id) ? change(r) : r)] };

        /// <summary>
        /// Sends the report as it stands to every client. Caller holds _lock, so the sends go out in
        /// the order the report changed; a client that misses one reads the report on reconnect.
        /// </summary>
        private void Publish(string kind)
        {
            var report = Report;
            var message = kind == "done" ? LiveMessageMapper.SetupCheckDone(report) : LiveMessageMapper.SetupCheckProgress(report);
            try
            {
                _ = full.Hub.Clients.Group(LiveGroups.Global).SetupCheck(message)
                    .ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch
            {
                // The hub not delivering is not the run's problem.
            }
        }

        // ── ffmpeg ───────────────────────────────────────────────────────────

        /// <summary>The live ffmpeg, as audio generation runs it, puts a 1 s tone through the post-process filters into an AAC .m4b.</summary>
        private async Task<IReadOnlyList<SetupCheckRow>> FullFfmpegAsync()
        {
            var paths = await ReadFfmpegPathsAsync(CancellationToken.None);
            var encode = await full.Encode.EncodeToneAsync(paths.Live, CancellationToken.None);
            // As in the Quick check: an appsettings ffmpeg that would do the job is the fix to offer.
            var fix = FfmpegFix;
            if (!encode.Success && paths.Note is not null
                && (await full.Encode.EncodeToneAsync(paths.Configured, CancellationToken.None)).Success)
                fix = new SetupFixHint("The appsettings path encodes: use it as the ffmpeg path.", AudioProcessing, paths.Configured);
            return
            [
                new SetupCheckRow(
                    SetupCheckIds.Ffmpeg, SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Full,
                    encode.Success ? SetupCheckResult.Passed : SetupCheckResult.Failed,
                    $"A 1 s tone through adynamicequalizer and alimiter, encoded to AAC .m4b by ffmpeg from {paths.Shown}",
                    encode.Message, paths.Note, fix, Managed: false),
            ];
        }
    }
}
