using Read2Me.App.Services.SetupCheck;
using Read2Me.Services.Events;
using Xunit;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The report's state rules, with no DI container, hub or docker: how a Quick check's rows merge
    /// with the last Full check's verdicts, what a cancel leaves, which run stamps the Full check's
    /// time, and the changes published on the way.
    /// </summary>
    public class SetupCheckLedgerTests
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = Start;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private readonly Clock _clock = new();
        private readonly RecordingSetupCheckChanges _changes = new();
        private readonly SetupCheckLedger _ledger;

        public SetupCheckLedgerTests() => _ledger = new SetupCheckLedger(_changes.Broadcaster, _clock);

        private static SetupCheckRow Ffmpeg(SetupCheckResult result, string found) => SetupChecks.Ffmpeg.Row(result, found);

        private static SetupCheckRow Transcription(SetupCheckResult result, string found) => SetupChecks.Transcription.Row(result, found);

        private static readonly SetupFullRun Step1 = new(1, 2, SetupCheckLane.Local, "Encoding a 1 s tone to .m4b", Cancelling: false);
        private static readonly SetupFullRun Step2 = new(2, 2, SetupCheckLane.Cpu, "Transcribing the sample clip", Cancelling: false);

        private void Quick(params SetupCheckRow[] rows) => _ledger.QuickChecked(rows, _ => null, estimateSeconds: 60);

        private SetupCheckRow Row(string id) => _ledger.Report.Rows.Single(r => r.Id == id);

        /// <summary>A whole run of the one ffmpeg step that answers <paramref name="verdict"/>.</summary>
        private void FullRun(SetupCheckRow verdict)
        {
            var run = _ledger.Claim(rerun: null, [SetupCheckIds.Ffmpeg], Step1 with { Total = 1 });
            _ledger.StepStarted(run, Step1 with { Total = 1 }, [SetupCheckIds.Ffmpeg]);
            _ledger.Judged([verdict]);
            _ledger.Finish(run, putBack: null);
        }

        // ── Verdict merge ────────────────────────────────────────────────────

        [Fact]
        public void A_Full_pass_stands_over_a_later_Quick_pass()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"));
            FullRun(Ffmpeg(SetupCheckResult.Passed, "Encoded a 1 s tone"));

            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"));

            Assert.Equal("Encoded a 1 s tone", Row(SetupCheckIds.Ffmpeg).Found);
        }

        [Fact]
        public void A_Quick_failure_overrides_a_Full_pass()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"));
            FullRun(Ffmpeg(SetupCheckResult.Passed, "Encoded a 1 s tone"));

            Quick(Ffmpeg(SetupCheckResult.Failed, "ffmpeg: not found"));

            Assert.Equal(SetupCheckResult.Failed, Row(SetupCheckIds.Ffmpeg).Result);
            Assert.Equal("ffmpeg: not found", Row(SetupCheckIds.Ffmpeg).Found);
        }

        [Fact]
        public void A_Full_failure_the_Quick_check_no_longer_finds_is_dropped_for_good()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"));
            FullRun(Ffmpeg(SetupCheckResult.Failed, "No such filter: 'adynamicequalizer'"));
            Assert.Equal(SetupCheckResult.Failed, Row(SetupCheckIds.Ffmpeg).Result);

            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.2"));
            Assert.Equal("ffmpeg version 7.2", Row(SetupCheckIds.Ffmpeg).Found);

            // Dropped, not shadowed: a Quick failure and then a pass never bring it back.
            Quick(Ffmpeg(SetupCheckResult.Failed, "ffmpeg: not found"));
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.3"));
            Assert.Equal("ffmpeg version 7.3", Row(SetupCheckIds.Ffmpeg).Found);
        }

        [Fact]
        public void A_Quick_check_s_rows_take_their_rerun_and_the_estimate()
        {
            _ledger.QuickChecked([Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1")], _ => new SetupRerun(Gpu: false), estimateSeconds: 42);

            Assert.Equal(new SetupRerun(Gpu: false), Row(SetupCheckIds.Ffmpeg).Rerun);
            Assert.Equal(42, _ledger.Report.FullCheckEstimateSeconds);
            Assert.Equal(Start, _ledger.Report.QuickCheckedAt);
        }

        [Fact]
        public void A_Quick_check_that_ends_while_a_run_goes_leaves_the_run_s_rows()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"));
            var run = _ledger.Claim(rerun: null, [SetupCheckIds.Ffmpeg], Step1 with { Total = 1 });
            _ledger.StepStarted(run, Step1 with { Total = 1 }, [SetupCheckIds.Ffmpeg]);

            Quick(Ffmpeg(SetupCheckResult.Failed, "ffmpeg: not found"));

            Assert.Equal(SetupCheckResult.Checking, Row(SetupCheckIds.Ffmpeg).Result);
            _ledger.Finish(run, putBack: null);
        }

        // ── Cancel ───────────────────────────────────────────────────────────

        [Fact]
        public async Task A_cancel_leaves_the_rows_of_steps_not_begun_Not_run_and_keeps_those_judged()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"), Transcription(SetupCheckResult.Passed, "Whisper · http://localhost:9000"));
            var run = _ledger.Claim(rerun: null, [SetupCheckIds.Ffmpeg, SetupCheckIds.Transcription], Step1);
            _ledger.StepStarted(run, Step1, [SetupCheckIds.Ffmpeg]);

            var cancelling = _ledger.Cancel();
            _ledger.Judged([Ffmpeg(SetupCheckResult.Passed, "Encoded a 1 s tone")]);
            Assert.True(run.Cancelled.IsCancellationRequested);
            _ledger.Finish(run, putBack: "Nothing to put back: the check drove no containers.");

            Assert.True(cancelling?.FullRun?.Cancelling);
            var report = _ledger.Report;
            Assert.Equal("Encoded a 1 s tone", Row(SetupCheckIds.Ffmpeg).Found);
            var transcription = Row(SetupCheckIds.Transcription);
            Assert.Equal(SetupCheckResult.NotRun, transcription.Result);
            Assert.Equal("Full check cancelled", transcription.Found);
            Assert.Null(report.FullRun);
            Assert.Equal("Nothing to put back: the check drove no containers.", report.PutBack);
            Assert.Equal(report, await run.Finished);
        }

        [Fact]
        public void A_row_a_begun_step_shares_with_a_skipped_one_keeps_its_verdict()
        {
            var container = SetupCheckIds.Container("whisper");
            Quick(Transcription(SetupCheckResult.Passed, "Whisper"), SetupChecks.Similarity.Row(SetupCheckResult.Passed, "MiniLM"),
                SetupCheckRowFor(container));
            var run = _ledger.Claim(rerun: null, [SetupCheckIds.Transcription, container, SetupCheckIds.Similarity, container], Step1);
            _ledger.StepStarted(run, Step1, [SetupCheckIds.Transcription, container]);
            _ledger.Cancel();
            _ledger.Judged([Transcription(SetupCheckResult.Passed, "Transcribed"), SetupCheckRowFor(container) with { Found = "Running, and answered" }]);
            _ledger.Finish(run, putBack: null);

            Assert.Equal("Running, and answered", Row(container).Found);
            Assert.Equal(SetupCheckResult.NotRun, Row(SetupCheckIds.Similarity).Result);
        }

        private static SetupCheckRow SetupCheckRowFor(string id) =>
            new SetupCheckDefinition(id, SetupCheckGroup.DockerServices, "read2me-whisper", SetupCheckTier.Quick, "", new SetupFixHint("", null, null))
                .Passed("Running and answering");

        [Fact]
        public void A_step_that_throws_fails_only_its_own_rows()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"), Transcription(SetupCheckResult.Passed, "Whisper"));
            var run = _ledger.Claim(rerun: null, [SetupCheckIds.Ffmpeg], Step1 with { Total = 1 });
            _ledger.StepStarted(run, Step1 with { Total = 1 }, [SetupCheckIds.Ffmpeg]);

            _ledger.StepFailed([SetupCheckIds.Ffmpeg], "The encode threw");
            _ledger.Finish(run, putBack: null);

            Assert.Equal(SetupCheckResult.Failed, Row(SetupCheckIds.Ffmpeg).Result);
            Assert.Equal("The encode threw", Row(SetupCheckIds.Ffmpeg).Found);
            Assert.Equal("Whisper", Row(SetupCheckIds.Transcription).Found);
        }

        [Fact]
        public void Cancel_with_no_run_going_answers_null_and_publishes_nothing()
        {
            Assert.Null(_ledger.Cancel());
            Assert.Empty(_changes.Changes);
        }

        // ── Stamping ─────────────────────────────────────────────────────────

        [Fact]
        public void A_whole_run_stamps_the_Full_check_s_time_and_a_re_run_keeps_it()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"));
            FullRun(Ffmpeg(SetupCheckResult.Passed, "Encoded a 1 s tone"));
            Assert.Equal(Start, _ledger.Report.FullCheckedAt);

            _clock.Now = Start.AddHours(1);
            var rerun = _ledger.Claim(SetupCheckIds.Ffmpeg, [SetupCheckIds.Ffmpeg], Step1 with { Total = 1 });
            _ledger.StepStarted(rerun, Step1 with { Total = 1 }, [SetupCheckIds.Ffmpeg]);
            _ledger.Judged([Ffmpeg(SetupCheckResult.Passed, "Encoded a 1 s tone again")]);
            _ledger.Finish(rerun, putBack: null);

            Assert.Equal(Start, _ledger.Report.FullCheckedAt);
            Assert.Equal("Encoded a 1 s tone again", Row(SetupCheckIds.Ffmpeg).Found);
        }

        [Fact]
        public void The_report_carries_the_start_s_stamp()
        {
            var ledger = new SetupCheckLedger(_changes.Broadcaster, _clock, report => report with { FirstRun = true, NoWorkspace = true });

            Assert.True(ledger.Report.FirstRun);
            Assert.True(ledger.Report.NoWorkspace);
        }

        // ── Row factories ────────────────────────────────────────────────────

        [Fact]
        public void A_Not_run_row_is_the_check_s_own_row_with_the_reason_and_the_way_on()
        {
            var fix = new SetupFixHint("Fix the Workspace row first, then restart the app.", null, null);

            var ffmpeg = SetupChecks.Ffmpeg.NotRun("needs the Workspace", fix);

            // The Quick check's ffmpeg row is Full-tier: it judges -version, the Full check the encode.
            Assert.Equal(
                new SetupCheckRow("ffmpeg", SetupCheckGroup.WorkspaceAndTools, "ffmpeg", SetupCheckTier.Full, SetupCheckResult.NotRun,
                    "ffmpeg -version and ffprobe -version", "needs the Workspace", null, fix, Managed: false, Rerun: null),
                ffmpeg);
        }

        [Fact]
        public void A_container_s_row_is_managed_Quick_tier_and_named_for_the_container()
        {
            var whisper = new Read2Me.Services.Health.DockerAiServiceRegistry().All.Single(s => s.Name == "whisper");

            var row = SetupChecks.Container(whisper).Passed("Running and answering");

            Assert.Equal(("container-whisper", SetupCheckGroup.DockerServices, "read2me-whisper", SetupCheckTier.Quick, true),
                (row.Id, row.Group, row.Name, row.Tier, row.Managed));
            Assert.Equal($"read2me-whisper running and answering at {whisper.BaseUrl}", row.Expected);
            Assert.Equal("docker logs -f read2me-whisper", row.Fix.Copy);
        }

        // ── Publishing ───────────────────────────────────────────────────────

        [Fact]
        public async Task Each_change_is_published_in_order_and_done_carries_the_final_report()
        {
            Quick(Ffmpeg(SetupCheckResult.Passed, "ffmpeg version 7.1"), Transcription(SetupCheckResult.Passed, "Whisper"));
            Assert.Empty(_changes.Changes);

            var run = _ledger.Claim(rerun: null, [SetupCheckIds.Ffmpeg, SetupCheckIds.Transcription], Step1);
            _ledger.StepStarted(run, Step1, [SetupCheckIds.Ffmpeg]);
            _ledger.Judged([Ffmpeg(SetupCheckResult.Passed, "Encoded a 1 s tone")]);
            _ledger.StepStarted(run, Step2, [SetupCheckIds.Transcription]);
            _ledger.Cancel();
            _ledger.Judged([Transcription(SetupCheckResult.Passed, "Transcribed")]);
            _ledger.Finish(run, putBack: null);

            var changes = _changes.Changes;
            Assert.Equal(
                new[] { SetupCheckChangeKind.Progress, SetupCheckChangeKind.Progress, SetupCheckChangeKind.Progress, SetupCheckChangeKind.Progress, SetupCheckChangeKind.Done },
                changes.Select(c => c.Kind));
            Assert.Equal(Step1, changes[0].Report.FullRun);
            Assert.Equal(SetupCheckResult.Passed, changes[0].Report.Rows[0].Result);
            Assert.Equal(SetupCheckResult.Checking, changes[1].Report.Rows[0].Result);
            Assert.Equal(Step2, changes[2].Report.FullRun);
            Assert.Equal("Encoded a 1 s tone", changes[2].Report.Rows[0].Found);
            Assert.Equal(SetupCheckResult.Checking, changes[2].Report.Rows[1].Result);
            Assert.True(changes[3].Report.FullRun?.Cancelling);
            Assert.Null(changes[4].Report.FullRun);
            Assert.Equal("Transcribed", changes[4].Report.Rows[1].Found);
            Assert.Equal(changes[4].Report, await run.Finished);
        }
    }
}
