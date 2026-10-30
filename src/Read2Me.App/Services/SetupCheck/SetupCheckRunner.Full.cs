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

        /// <summary>The run going; its cancel stops it after the current step.</summary>
        private sealed class FullRun
        {
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

        public async Task<FullCheckStart> StartFullCheckAsync()
        {
            await _starting.WaitAsync();
            try
            {
                lock (_lock)
                {
                    if (_run is { } going)
                        return new FullCheckStart(Report, Joined: true, Refusal: null, going.Finished.Task);
                    if (FullRefusal() is { } refusal)
                        return new FullCheckStart(Report, Joined: false, refusal, Task.FromResult(Report));
                }

                var context = await ReadFullContextAsync();

                FullRun run;
                IReadOnlyList<FullStep> steps;
                SetupCheckReport started;
                lock (_lock)
                {
                    // Work may have arrived while the configs were read.
                    if (FullRefusal() is { } refusal)
                        return new FullCheckStart(Report, Joined: false, refusal, Task.FromResult(Report));

                    context.Before = _report.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
                    run = _run = new FullRun();
                    steps = Plan(context);
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
                    _report = _report with { FullRun = null, FullCheckedAt = _clock.GetUtcNow(), PutBack = putBack };
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
                        .Select(row => row with { Result = SetupCheckResult.Failed, Found = ex.Message, Tier = SetupCheckTier.Full })];
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
                && (await full.Encode.EncodeToneAsync(paths.Seeded, CancellationToken.None)).Success)
                fix = new SetupFixHint("The appsettings path encodes: use it as the ffmpeg path.", AudioProcessing, paths.Seeded);
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
