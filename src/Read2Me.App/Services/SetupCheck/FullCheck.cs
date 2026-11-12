using Read2Me.Services.Health;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What the Full check reads and drives: the Quick check's services, the gates, work, busy flag,
    /// watchdog and encode of <see cref="FullCheckServices"/>, the Workspace this start runs on, and
    /// the ledger it reports into.
    /// </summary>
    internal sealed record FullCheckDeps(
        SetupCheckServices AiServices, FullCheckServices Full, WorkspaceStart WorkspaceStart, SetupCheckLedger Ledger);

    /// <summary>
    /// What a Quick check takes from the plan: each row's rerun, and how long a Full check is likely to take.
    /// </summary>
    internal sealed record FullCheckPreview(Func<SetupCheckRow, SetupRerun?> Rerun, int EstimateSeconds);

    /// <summary>
    /// The Full check: one run per host, in the background, as a list of steps. While it runs every
    /// queue gate is closed and Preflight refuses new AI tasks (both with "Setup check running"),
    /// and the watchdog holds off the services the steps drive. Each step marks its rows
    /// Checking, runs on its own (a throw fails only its rows) and replaces them; every change goes
    /// to every client as a <c>setupCheck</c> message. A cancel lets the current step finish; the
    /// steps after it read Not run. Results live in memory only; the report's state is the
    /// <see cref="SetupCheckLedger"/>'s.
    /// </summary>
    internal sealed partial class FullCheck(FullCheckDeps deps)
    {
        /// <summary>The plan for these configs, as a Quick check shows it: each row's rerun and the estimate.</summary>
        public FullCheckPreview Preview(ActiveAiConfigs configs, IReadOnlyDictionary<string, AiServiceStatus> statuses)
        {
            var plan = Plan(new FullContext(configs));
            return new FullCheckPreview(row => Rerun(row, plan), EstimateSeconds(configs, plan, statuses));
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

        /// <summary>
        /// Starts a whole Full check (<paramref name="rerun"/> null), which joins a whole one going, or
        /// a re-run of one check: only the steps that judge that row. The ledger admits and claims the
        /// run; between the two, the work in flight and the plan can still refuse it.
        /// </summary>
        public async Task<FullCheckStart> StartAsync(string? rerun)
        {
            await _starting.WaitAsync();
            try
            {
                if (deps.Ledger.Admit(rerun) is { } admitted)
                    return admitted;
                if (FullRefusal() is { } busy)
                    return deps.Ledger.Refused(busy);

                // Read fresh: the configs may have changed since the Quick check.
                var context = new FullContext(await ActiveAiConfigs.ReadAsync(deps.AiServices));

                // Work may have arrived while the configs were read.
                if (FullRefusal() is { } refusal)
                    return deps.Ledger.Refused(refusal);
                var steps = rerun is null ? Plan(context) : StepsFor(Plan(context), rerun);
                if (steps.Count == 0)
                    return deps.Ledger.Refused($"{deps.Ledger.Row(rerun!)?.Name ?? rerun} has nothing for the Full check to send: it has no active config.");
                // The web app confirms a GPU re-run on the report's rerun.gpu, which the last Quick
                // check set: a config moved onto a GPU container since must not skip that confirm.
                if (rerun is not null && deps.Ledger.Row(rerun) is { Rerun: not { Gpu: true } } stale && DrivesGpu(steps))
                    return deps.Ledger.Refused($"{stale.Name} now drives a GPU container: run the Quick check, then re-run it.");

                var run = deps.Ledger.Claim(rerun, [.. steps.SelectMany(s => s.RowIds)],
                    new SetupFullRun(1, steps.Count, steps[0].Lane, steps[0].Label, Cancelling: false));
                context.Before = run.Before;
                deps.Full.Busy.Set(true);
                deps.Full.CharacterGate.Close(GateOwners.SetupCheck, SetupCheckBusy.Reason);
                deps.Full.AudioGate.Close(GateOwners.SetupCheck, SetupCheckBusy.Reason);

                _ = Task.Run(() => RunFullAsync(run, steps, context));
                return new FullCheckStart(run.Started, Joined: false, Refusal: null, run.Finished);
            }
            finally
            {
                _starting.Release();
            }
        }

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
            var gpu = deps.AiServices.Registry.All.Where(s => s.UsesGpu).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return steps.Any(s => s.Services.Any(gpu.Contains));
        }

        /// <summary>
        /// What re-running a row takes: null when it isn't Full-tier or no step judges it (no active
        /// config), and whether a step drives a GPU container, which the web app confirms first.
        /// </summary>
        private SetupRerun? Rerun(SetupCheckRow row, IReadOnlyList<FullStep> plan) =>
            row.Tier == SetupCheckTier.Full && StepsFor(plan, row.Id) is { Count: > 0 } steps
                ? new SetupRerun(Gpu: DrivesGpu(steps))
                : null;

        private string? FullRefusal() => deps.WorkspaceStart.Usable
            ? deps.Full.Work.Refusal()
            : "The Full check needs the Workspace: fix the Workspace row, then restart the app.";

        /// <summary>
        /// Runs the steps, then puts back; then opens the gates and clears Busy, and only then has the
        /// ledger end the run, so a start that lands in between joins a run that is all but done.
        /// </summary>
        private async Task RunFullAsync(SetupCheckLedger.Run run, IReadOnlyList<FullStep> steps, FullContext context)
        {
            string? putBack = null;
            try
            {
                // Held for the whole run, putting back included: a service one step stops for another
                // must not trip the watchdog.
                using var hold = deps.Full.Watchdog.HoldOff(
                    [.. steps.SelectMany(s => s.Services).Distinct(StringComparer.OrdinalIgnoreCase)]);
                try
                {
                    for (var next = 0; next < steps.Count && !run.Cancelled.IsCancellationRequested; next++)
                        await RunStepAsync(run, steps[next], next + 1, steps.Count);
                }
                finally
                {
                    putBack = await PutBackAsync(context);
                }
            }
            finally
            {
                deps.Full.CharacterGate.Open(GateOwners.SetupCheck);
                deps.Full.AudioGate.Open(GateOwners.SetupCheck);
                deps.Full.Busy.Set(false);
                deps.Ledger.Finish(run, putBack);
            }
        }

        /// <summary>One step: its rows read Checking, then its verdicts replace them.</summary>
        private async Task RunStepAsync(SetupCheckLedger.Run run, FullStep step, int number, int total)
        {
            deps.Ledger.StepStarted(run, new SetupFullRun(number, total, step.Lane, step.Label, Cancelling: false), step.RowIds);
            try
            {
                deps.Ledger.Judged(await step.RunAsync());
            }
            catch (Exception ex)
            {
                // Each step stands alone: a throw fails its own rows and the run goes on.
                deps.Ledger.StepFailed(step.RowIds, ex.Message);
            }
        }

        // ── ffmpeg ───────────────────────────────────────────────────────────

        /// <summary>The live ffmpeg, as audio generation runs it, puts a 1 s tone through the post-process filters into an AAC .m4b.</summary>
        private async Task<IReadOnlyList<SetupCheckRow>> FullFfmpegAsync()
        {
            var paths = await FfmpegPaths.ReadAsync(deps.AiServices.Scopes, CancellationToken.None);
            var encode = await deps.Full.Encode.EncodeToneAsync(paths.Live, CancellationToken.None);
            // As in the Quick check: an appsettings ffmpeg that would do the job is the fix to offer.
            SetupFixHint? fix = null;
            if (!encode.Success && paths.Note is not null
                && (await deps.Full.Encode.EncodeToneAsync(paths.Configured, CancellationToken.None)).Success)
                fix = new SetupFixHint("The appsettings path encodes: use it as the ffmpeg path.", SetupSettingsLink.AudioProcessing, paths.Configured);
            return
            [
                SetupChecks.Ffmpeg.Row(encode.Success ? SetupCheckResult.Passed : SetupCheckResult.Failed, encode.Message, paths.Note, fix) with
                {
                    Expected = $"A 1 s tone through adynamicequalizer and alimiter, encoded to AAC .m4b by ffmpeg from {paths.Shown}",
                },
            ];
        }
    }
}
