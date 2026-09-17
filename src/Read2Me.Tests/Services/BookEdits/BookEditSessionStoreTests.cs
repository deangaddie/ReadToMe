using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class BookEditSessionStoreTests
    {
        private static readonly ProjectFolderId Folder = new("book-a");
        private readonly ManualTimeProvider _clock = new();

        private static EditProgram Program(TransformKind kind = TransformKind.Llm) =>
            new(true, null, EditTargetSelector.ChapterTitle, NodeFilter.All, ParagraphFilter.All,
                new EditTransform(kind, Instruction: "capitalise"));

        private static EditTarget Target(int n) =>
            new(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), $"chapter {n}", $"Chapter {n}", n, null, null);

        private static ProposedEdit Row(EditTarget t) =>
            new(t.Kind, t.Id, t.DisplayPath, t.CurrentValue, t.CurrentValue.ToUpperInvariant(), ProposalStatus.Proposed, null);

        [Fact]
        public void Create_then_TryGet_answers_the_session_with_its_plan()
        {
            var store = new BookEditSessionStore(_clock);
            var program = Program();
            var targets = new[] { Target(1), Target(2) };

            var created = store.Create(Folder, program, targets, "Rename every chapter");
            var found = store.TryGet(Folder, created.Id);

            Assert.Same(created, found);
            Assert.Same(program, found!.Program);
            Assert.Equal(targets, found.Targets);
            Assert.Equal("Rename every chapter", found.Summary);
            Assert.Equal(BookEditRunStatus.Idle, found.Snapshot().Status);
        }

        [Fact]
        public void Session_ids_are_url_safe_and_unique()
        {
            var store = new BookEditSessionStore(_clock);

            var a = store.Create(Folder, Program(), [Target(1)], "s");
            var b = store.Create(Folder, Program(), [Target(1)], "s");

            Assert.NotEqual(a.Id, b.Id);
            Assert.Matches("^[0-9a-f]{32}$", a.Id);
        }

        [Fact]
        public void TryGet_is_scoped_to_the_folder_that_created_the_session()
        {
            var store = new BookEditSessionStore(_clock);
            var session = store.Create(Folder, Program(), [Target(1)], "s");

            Assert.Null(store.TryGet(new ProjectFolderId("book-b"), session.Id));
            Assert.Null(store.TryGet(Folder, "nope"));
        }

        [Fact]
        public void Sessions_expire_after_the_ttl_and_a_lookup_extends_the_life()
        {
            var store = new BookEditSessionStore(_clock, TimeSpan.FromMinutes(10));
            var session = store.Create(Folder, Program(), [Target(1)], "s");

            _clock.Advance(TimeSpan.FromMinutes(9));
            Assert.NotNull(store.TryGet(Folder, session.Id));
            _clock.Advance(TimeSpan.FromMinutes(9));
            Assert.NotNull(store.TryGet(Folder, session.Id)); // the lookup at 9 min reset the clock
            _clock.Advance(TimeSpan.FromMinutes(11));
            Assert.Null(store.TryGet(Folder, session.Id));
        }

        [Fact]
        public void Remove_drops_the_session_and_cancels_a_running_proposal()
        {
            var store = new BookEditSessionStore(_clock);
            var session = store.Create(Folder, Program(), [Target(1)], "s");
            Assert.True(session.TryBeginRun(out var ct));

            Assert.True(store.Remove(Folder, session.Id));

            Assert.True(ct.IsCancellationRequested);
            Assert.Null(store.TryGet(Folder, session.Id));
            Assert.False(store.Remove(Folder, session.Id));
        }

        [Fact]
        public void A_run_reports_progress_then_lands_its_rows()
        {
            var session = new BookEditSessionStore(_clock).Create(Folder, Program(), [Target(1), Target(2)], "s");

            Assert.True(session.TryBeginRun(out _));
            Assert.Equal(BookEditRunStatus.Running, session.Snapshot().Status);
            session.ReportProgress(1);
            Assert.Equal((1, 2), (session.Snapshot().Done, session.Snapshot().Total));

            var rows = session.Targets.Select(Row).ToList();
            session.CompleteRun(rows, cancelled: false);

            var snapshot = session.Snapshot();
            Assert.Equal(BookEditRunStatus.Completed, snapshot.Status);
            Assert.Equal(2, snapshot.Done);
            Assert.Equal(rows, snapshot.Rows);
        }

        [Fact]
        public void Only_one_run_at_a_time_and_a_finished_run_can_be_started_again()
        {
            var session = new BookEditSessionStore(_clock).Create(Folder, Program(), [Target(1)], "s");

            Assert.True(session.TryBeginRun(out _));
            Assert.False(session.TryBeginRun(out _));

            session.CompleteRun([], cancelled: false);
            Assert.True(session.TryBeginRun(out _));
        }

        [Fact]
        public void Cancel_trips_the_token_and_the_partial_rows_stay_reviewable()
        {
            var session = new BookEditSessionStore(_clock).Create(Folder, Program(), [Target(1), Target(2)], "s");
            session.TryBeginRun(out var ct);
            var partial = new[] { Row(session.Targets[0]) };

            session.CancelRun();
            Assert.True(ct.IsCancellationRequested);
            session.CompleteRun(partial, cancelled: true);

            var snapshot = session.Snapshot();
            Assert.Equal(BookEditRunStatus.Cancelled, snapshot.Status);
            Assert.Equal(1, snapshot.Done);
            Assert.Single(snapshot.Rows);
        }

        [Fact]
        public void A_failed_run_keeps_the_reason()
        {
            var session = new BookEditSessionStore(_clock).Create(Folder, Program(), [Target(1)], "s");
            session.TryBeginRun(out _);

            session.FailRun("boom");

            var snapshot = session.Snapshot();
            Assert.Equal(BookEditRunStatus.Failed, snapshot.Status);
            Assert.Equal("boom", snapshot.Reason);
            Assert.True(session.TryBeginRun(out _));
        }

        [Fact]
        public void FindTarget_matches_on_kind_and_id()
        {
            var targets = new[] { Target(1), Target(2) };
            var session = new BookEditSessionStore(_clock).Create(Folder, Program(), targets, "s");

            Assert.Same(targets[1], session.FindTarget(targets[1].Id));
            Assert.Null(session.FindTarget(Guid.NewGuid()));
        }
    }
}
