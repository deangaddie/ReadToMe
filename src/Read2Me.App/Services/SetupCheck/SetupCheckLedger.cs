using Read2Me.Services.Events;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Setup check report's state, under its own lock: the rows and the last Full check's verdicts
    /// (merged into each Quick check's rows), the one run going with its cancel and its end, the
    /// progress strip, Checking and Not run on cancel, the Full check's time (a whole run stamps it, a
    /// re-run doesn't), and the put-back line. Every change a run makes is published in the order it
    /// was made. It never probes, drives a container or touches a gate: the Full check steps do.
    /// </summary>
    internal sealed class SetupCheckLedger(
        EventBroadcaster<SetupCheckChanged> changes,
        TimeProvider clock,
        Func<SetupCheckReport, SetupCheckReport>? stamp = null)
    {
        private const string Cancelled = "Full check cancelled";

        private readonly object _lock = new();
        // Guarded by _lock.
        private SetupCheckReport _report = SetupCheckReport.Empty;
        private Run? _run;
        private readonly Dictionary<string, SetupCheckRow> _fullVerdicts = new(StringComparer.Ordinal);

        /// <summary>
        /// The run going. <see cref="Rerun"/>: the one check it re-runs, null for a whole Full check.
        /// <see cref="Before"/>: the rows as the run found them. <see cref="Cancelled"/> stops it after
        /// the current step; <see cref="Finished"/> completes with the final report.
        /// </summary>
        internal sealed class Run(string? rerun, IReadOnlyCollection<string> rowIds, IReadOnlyDictionary<string, SetupCheckRow> before)
        {
            private readonly CancellationTokenSource _cancel = new();
            private readonly TaskCompletionSource<SetupCheckReport> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string? Rerun { get; } = rerun;
            public IReadOnlyDictionary<string, SetupCheckRow> Before { get; } = before;
            public CancellationToken Cancelled => _cancel.Token;
            public Task<SetupCheckReport> Finished => _finished.Task;

            /// <summary>The report as the run started, progress strip on.</summary>
            public SetupCheckReport Started { get; internal set; } = SetupCheckReport.Empty;

            /// <summary>Every row the run's steps judge, and those of the steps begun so far.</summary>
            internal IReadOnlyCollection<string> RowIds { get; } = rowIds;
            internal HashSet<string> Begun { get; } = new(StringComparer.Ordinal);

            internal void Cancel() => _cancel.Cancel();

            internal void End(SetupCheckReport report)
            {
                _finished.TrySetResult(report);
                _cancel.Dispose();
            }
        }

        /// <summary>The report as it stands, with the start's stamp.</summary>
        public SetupCheckReport Report
        {
            get { lock (_lock) return Stamped(); }
        }

        public bool Running
        {
            get { lock (_lock) return _run is not null; }
        }

        public SetupCheckRow? Row(string id)
        {
            lock (_lock) return RowOrNull(id);
        }

        /// <summary>
        /// A Quick check's rows replace the report's, with the last Full check's verdicts laid over
        /// them and each row's rerun set. A Full pass is the stronger proof, so it stands unless the
        /// Quick check now fails the row. A Full failure the Quick check no longer finds is dropped:
        /// the fix may have worked, and the next Full check proves it. Ignored while a run goes: the
        /// run owns the rows until it ends.
        /// </summary>
        public void QuickChecked(IReadOnlyList<SetupCheckRow> rows, Func<SetupCheckRow, SetupRerun?> rerun, int? estimateSeconds)
        {
            lock (_lock)
            {
                if (_run is not null)
                    return;
                _report = _report with
                {
                    Rows = [.. WithFullVerdicts(rows).Select(row => row with { Rerun = rerun(row) })],
                    QuickCheckedAt = clock.GetUtcNow(),
                    FullCheckEstimateSeconds = estimateSeconds ?? _report.FullCheckEstimateSeconds,
                };
            }
        }

        /// <summary>
        /// Whether a start may go on to claim a run: null when it may; else its refusal, or the run
        /// going for a whole Full check to join. A re-run needs the start-up Quick check's rows and a
        /// Full-tier row to re-run; a whole check can't join a re-run, which checks one row.
        /// </summary>
        public FullCheckStart? Admit(string? rerun)
        {
            lock (_lock)
            {
                if (rerun is not null && _report.QuickCheckedAt is null)
                    return RefusedLocked("The start-up Quick check hasn't ended: re-run the check once it has.");
                if (rerun is not null && RowOrNull(rerun) is not { Tier: SetupCheckTier.Full })
                    return RefusedLocked($"There's no Full-tier check \"{rerun}\" to re-run.", unknownCheck: true);
                if (_run is not { } going)
                    return null;
                return (going.Rerun, rerun) switch
                {
                    (null, null) => new FullCheckStart(Stamped(), Joined: true, Refusal: null, going.Finished),
                    (null, _) => RefusedLocked("A Full check is running: re-run the check once it ends."),
                    (_, null) => RefusedLocked("A check is re-running: run the Full check once it ends."),
                    _ => RefusedLocked("A check is re-running: re-run this one once it ends."),
                };
            }
        }

        /// <summary>A start refused: nothing ran, so it finishes at once on the report as it stands.</summary>
        public FullCheckStart Refused(string refusal, bool unknownCheck = false)
        {
            lock (_lock) return RefusedLocked(refusal, unknownCheck);
        }

        /// <summary>
        /// Claims the run: the rows its steps judge (<paramref name="rowIds"/>) and its first step's
        /// progress. Clears the last put-back line and publishes progress. Starts are serialized by
        /// the caller, which asked <see cref="Admit"/> first, so no run is going.
        /// </summary>
        public Run Claim(string? rerun, IReadOnlyCollection<string> rowIds, SetupFullRun first)
        {
            lock (_lock)
            {
                if (_run is not null)
                    throw new InvalidOperationException("A Setup check run is already going.");
                var run = _run = new Run(rerun, rowIds, _report.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal));
                _report = _report with { FullRun = first with { Cancelling = false }, PutBack = null };
                run.Started = Stamped();
                Publish(SetupCheckChangeKind.Progress);
                return run;
            }
        }

        /// <summary>A step begins: the progress strip shows it, and its rows read Checking.</summary>
        public void StepStarted(Run run, SetupFullRun step, IReadOnlyList<string> rowIds)
        {
            lock (_lock)
            {
                run.Begun.UnionWith(rowIds);
                // A cancel that lands after the run's check still shows; the step still runs.
                _report = _report with { FullRun = step with { Cancelling = run.Cancelled.IsCancellationRequested } };
                SetRows(rowIds, row => row with { Result = SetupCheckResult.Checking });
                Publish(SetupCheckChangeKind.Progress);
            }
        }

        /// <summary>A step's verdicts replace its rows, and stand over later Quick checks as <see cref="QuickChecked"/> says.</summary>
        public void Judged(IReadOnlyList<SetupCheckRow> rows)
        {
            lock (_lock)
                JudgedLocked(rows);
        }

        /// <summary>A step that threw fails its own rows with the message; the run goes on.</summary>
        public void StepFailed(IReadOnlyList<string> rowIds, string found)
        {
            lock (_lock)
                JudgedLocked([.. rowIds.Select(RowOrNull).OfType<SetupCheckRow>()
                    .Select(row => row with { Result = SetupCheckResult.Failed, Found = found })]);
        }

        /// <summary>Asks the run going to stop once its current step ends. Null when none is going.</summary>
        public SetupCheckReport? Cancel()
        {
            lock (_lock)
            {
                if (_run is null)
                    return null;
                _run.Cancel();
                if (_report.FullRun is { } going)
                    _report = _report with { FullRun = going with { Cancelling = true } };
                Publish(SetupCheckChangeKind.Progress);
                return Stamped();
            }
        }

        /// <summary>
        /// Ends the run: rows no begun step judged read Not run, a whole run stamps the Full check's
        /// time (a re-run judges one check, so the time stays the last whole one's), the put-back line
        /// shows, and done is published with the final report.
        /// </summary>
        public void Finish(Run run, string? putBack)
        {
            lock (_lock)
            {
                SetRows([.. run.RowIds.Where(id => !run.Begun.Contains(id))],
                    row => row with { Result = SetupCheckResult.NotRun, Found = Cancelled, Note = null });
                _report = _report with
                {
                    FullRun = null,
                    FullCheckedAt = run.Rerun is null ? clock.GetUtcNow() : _report.FullCheckedAt,
                    PutBack = putBack,
                };
                _run = null;
                Publish(SetupCheckChangeKind.Done);
                run.End(Stamped());
            }
        }

        // ── Caller holds _lock ───────────────────────────────────────────────

        private SetupCheckReport Stamped() => stamp is null ? _report : stamp(_report);

        private FullCheckStart RefusedLocked(string refusal, bool unknownCheck = false)
        {
            var report = Stamped();
            return new(report, Joined: false, refusal, Task.FromResult(report), unknownCheck);
        }

        private void JudgedLocked(IReadOnlyList<SetupCheckRow> rows)
        {
            foreach (var row in rows)
            {
                _fullVerdicts[row.Id] = row;
                SetRows([row.Id], _ => row);
            }
        }

        private IEnumerable<SetupCheckRow> WithFullVerdicts(IReadOnlyList<SetupCheckRow> rows)
        {
            foreach (var row in rows)
            {
                if (row.Result == SetupCheckResult.Failed || !_fullVerdicts.TryGetValue(row.Id, out var verdict))
                    yield return row;
                else if (verdict.Result != SetupCheckResult.Failed)
                    yield return verdict;
                else
                {
                    _fullVerdicts.Remove(row.Id);
                    yield return row;
                }
            }
        }

        private SetupCheckRow? RowOrNull(string id) => _report.Rows.FirstOrDefault(r => r.Id == id);

        private void SetRows(IReadOnlyCollection<string> ids, Func<SetupCheckRow, SetupCheckRow> change) =>
            _report = _report with { Rows = [.. _report.Rows.Select(r => ids.Contains(r.Id) ? change(r) : r)] };

        /// <summary>Publishes the report as it stands; under the lock, so changes go out in the order they were made.</summary>
        private void Publish(SetupCheckChangeKind kind) => changes.Publish(new SetupCheckChanged(kind, Stamped()));
    }
}
