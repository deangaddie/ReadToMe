using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    /// <summary>
    /// The Edit plan use case as a whole: the Restore race (refused while one runs, and when one began
    /// at any point after planning started), the session it keeps or drops, the request count and the
    /// warnings. The planner and resolver are faked; what they answer is BookEditPlannerTests and
    /// ScopeResolverTests.
    /// </summary>
    public class BookEditPlanServiceTests
    {
        private static readonly ProjectFolderId Folder = new("book-a");

        private readonly ProjectActivity _activity = new(() => new NotBusy());
        private readonly BookEditSessionStore _sessions = new(TimeProvider.System);
        private readonly FakePlanner _planner = new();
        private readonly FakeResolver _resolver = new();

        private BookEditPlanService NewService() => new(_planner, _resolver, _sessions, _activity);

        private static EditProgram Program(TransformKind kind = TransformKind.Llm) =>
            new(true, null, EditTargetSelector.ChapterTitle, NodeFilter.All, ParagraphFilter.All,
                kind == TransformKind.Llm
                    ? new EditTransform(kind, Instruction: "capitalise")
                    : new EditTransform(kind, Template: "Chapter {n}"));

        private static List<EditTarget> Targets(int count) =>
            [.. Enumerable.Range(1, count).Select(n =>
                new EditTarget(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), $"chapter {n}", $"Chapter {n}", n, null, null))];

        private void PlansOk(TransformKind kind = TransformKind.Llm, int targets = 2)
        {
            _planner.Answer = () => new EditPlanOutcome(EditPlanStatus.Ok, Program(kind), null);
            _resolver.Targets = () => Targets(targets);
        }

        private Task<EditPlanResult> PlanAsync(bool thinking = true) =>
            NewService().PlanAsync(Folder, "capitalise every chapter title", thinking, TestContext.Current.CancellationToken);

        [Fact]
        public async Task A_plan_keeps_a_session_with_the_program_targets_and_summary()
        {
            PlansOk(targets: 3);

            var planned = Assert.IsType<EditPlanResult.Planned>(await PlanAsync());

            Assert.Same(planned.Session, _sessions.TryGet(Folder, planned.Session.Id));
            Assert.Equal(3, planned.Session.Targets.Count);
            Assert.Equal(EditProgramDescriber.Describe(planned.Session.Program), planned.Session.Summary);
            Assert.Empty(planned.Warnings);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Thinking_reaches_the_planner_as_its_opposite(bool thinking, bool disableThinking)
        {
            PlansOk();

            await PlanAsync(thinking);

            Assert.Equal(disableThinking, _planner.DisableThinking);
        }

        [Fact]
        public async Task A_Restore_running_at_the_start_is_refused_before_the_planner_is_asked()
        {
            PlansOk();
            Assert.Null(_activity.TryBeginRestore(Folder));

            var refused = Assert.IsType<EditPlanResult.Refused>(await PlanAsync());

            Assert.Equal(ActivityRefusalKind.Restoring, refused.Refusal.Kind);
            Assert.False(_planner.Asked);
            Assert.Empty(_sessions.RemoveAll(Folder));
        }

        [Fact]
        public async Task A_Restore_beginning_during_planning_is_refused_and_keeps_no_session()
        {
            PlansOk();
            _planner.During = () => Assert.Null(_activity.TryBeginRestore(Folder));

            var refused = Assert.IsType<EditPlanResult.Refused>(await PlanAsync());

            Assert.Equal(ProjectActivity.RestoringReason, refused.Refusal.Reason);
            Assert.Empty(_sessions.RemoveAll(Folder));
        }

        [Fact]
        public async Task A_Restore_beginning_during_resolving_is_refused_and_keeps_no_session()
        {
            PlansOk();
            _resolver.During = () => Assert.Null(_activity.TryBeginRestore(Folder));

            Assert.IsType<EditPlanResult.Refused>(await PlanAsync());

            Assert.Empty(_sessions.RemoveAll(Folder));
        }

        [Fact]
        public async Task A_whole_Restore_while_planning_is_refused_because_the_targets_would_predate_it()
        {
            PlansOk();
            _planner.During = () =>
            {
                Assert.Null(_activity.TryBeginRestore(Folder));
                _activity.EndRestore(Folder);
            };

            Assert.IsType<EditPlanResult.Refused>(await PlanAsync());

            Assert.Empty(_sessions.RemoveAll(Folder));
        }

        [Fact]
        public async Task A_Restore_of_another_project_does_not_refuse()
        {
            PlansOk();
            _planner.During = () => Assert.Null(_activity.TryBeginRestore(new ProjectFolderId("book-b")));

            Assert.IsType<EditPlanResult.Planned>(await PlanAsync());
        }

        [Theory]
        [InlineData(EditPlanStatus.Unsupported)]
        [InlineData(EditPlanStatus.NoLlmConfigured)]
        [InlineData(EditPlanStatus.ServiceUnavailable)]
        [InlineData(EditPlanStatus.Failed)]
        public async Task A_planner_failure_is_not_planned_with_its_reason_and_resolves_nothing(EditPlanStatus status)
        {
            _planner.Answer = () => new EditPlanOutcome(status, null, "why not");

            var notPlanned = Assert.IsType<EditPlanResult.NotPlanned>(await PlanAsync());

            Assert.Equal(status, notPlanned.Status);
            Assert.Equal("why not", notPlanned.Reason);
            Assert.False(_resolver.Asked);
        }

        [Fact]
        public async Task A_plan_that_matches_nothing_is_no_targets_and_keeps_no_session()
        {
            PlansOk(targets: 0);

            var notPlanned = Assert.IsType<EditPlanResult.NotPlanned>(await PlanAsync());

            Assert.Equal(EditPlanStatus.NoTargets, notPlanned.Status);
            Assert.False(string.IsNullOrEmpty(notPlanned.Reason));
            Assert.Empty(_sessions.RemoveAll(Folder));
        }

        [Fact]
        public async Task An_AI_plan_counts_the_requests_the_run_will_send()
        {
            PlansOk(targets: 20);

            var planned = Assert.IsType<EditPlanResult.Planned>(await PlanAsync());

            Assert.Equal(BatchPacker.RequestCount(planned.Session.Targets), planned.RequestCount);
            Assert.True(planned.RequestCount > 1);
        }

        [Fact]
        public async Task A_deterministic_plan_sends_no_requests()
        {
            PlansOk(TransformKind.SetTemplate, targets: 20);

            var planned = Assert.IsType<EditPlanResult.Planned>(await PlanAsync());

            Assert.Equal(0, planned.RequestCount);
        }

        [Theory]
        [InlineData(TransformKind.Llm, BookEditPlanService.LlmTargetWarnThreshold, false)]
        [InlineData(TransformKind.Llm, BookEditPlanService.LlmTargetWarnThreshold + 1, true)]
        [InlineData(TransformKind.SetTemplate, BookEditPlanService.LlmTargetWarnThreshold + 1, false)]
        public async Task Only_an_AI_plan_over_the_threshold_warns_that_the_job_is_large(
            TransformKind kind, int targets, bool warns)
        {
            PlansOk(kind, targets);

            var planned = Assert.IsType<EditPlanResult.Planned>(await PlanAsync());

            Assert.Equal(warns, planned.Warnings.Any(w => w.Contains("large AI job")));
        }

        [Fact]
        public async Task Titles_the_pattern_also_matches_add_their_warning()
        {
            PlansOk();
            _resolver.TitleCounts = new TitleMatchCounts(0, 2, 3);

            var planned = Assert.IsType<EditPlanResult.Planned>(await PlanAsync());

            Assert.Equal([EditProgramDescriber.TitlesAlsoMatch(_resolver.TitleCounts)!], planned.Warnings);
        }

        private sealed class NotBusy : IProjectBusyAnswer
        {
            public ProjectBusy? For(ProjectFolderId folder) => null;
        }

        private sealed class FakePlanner() : BookEditPlanner(null!, null!, null!, null!, null!)
        {
            public Func<EditPlanOutcome> Answer { get; set; } = () => throw new InvalidOperationException("No plan set.");
            public Action? During { get; set; }
            public bool Asked { get; private set; }
            public bool? DisableThinking { get; private set; }

            public override Task<EditPlanOutcome> PlanAsync(
                ProjectFolderId folderId, string instruction, bool disableThinking, CancellationToken ct)
            {
                Asked = true;
                DisableThinking = disableThinking;
                During?.Invoke();
                return Task.FromResult(Answer());
            }
        }

        private sealed class FakeResolver() : ScopeResolver(null!)
        {
            public Func<IReadOnlyList<EditTarget>> Targets { get; set; } = () => [];
            public Action? During { get; set; }
            public bool Asked { get; private set; }
            public TitleMatchCounts TitleCounts { get; set; } = new(0, 0, 0);

            public override Task<IReadOnlyList<EditTarget>> ResolveAsync(
                ProjectFolderId folderId, EditProgram program, CancellationToken ct = default)
            {
                Asked = true;
                During?.Invoke();
                return Task.FromResult(Targets());
            }

            public override Task<TitleMatchCounts> CountTitlesAlsoMatchingAsync(
                ProjectFolderId folderId, EditProgram program, CancellationToken ct = default) =>
                Task.FromResult(TitleCounts);
        }
    }
}
