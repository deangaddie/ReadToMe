using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// The <b>Project activity</b> gate's policy table (spec #112, "Policy"): every action in every state
/// — idle, busy, restoring, and both — through the gate's public questions, with busy faked by a
/// registered source; and the start protocol (#359) against a Restore and a delete, with no queue built.
/// </summary>
public class ProjectActivityTests
{
    private static readonly ProjectFolderId Folder = new("Book");
    private static readonly ProjectFolderId Other = new("Other");
    private const string BusyReason = "Audio Queue: 3 paragraphs";

    private readonly FakeBusy _busy = new();
    private readonly ProjectActivity _activity = new();

    public ProjectActivityTests() => _activity.Register(_busy);

    public enum State { Idle, Busy, Restoring, BusyAndRestoring, Deleting }

    public enum Act { Write, Start, ViewStep, StartRestore, PlanEdit, StartDelete }

    public static TheoryData<Act, State, ActivityRefusalKind?> Policy => new()
    {
        { Act.Write, State.Idle, null },
        { Act.Write, State.Busy, null },
        { Act.Write, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.Write, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.Write, State.Deleting, ActivityRefusalKind.Deleting },
        { Act.Start, State.Idle, null },
        { Act.Start, State.Busy, null },
        { Act.Start, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.Start, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.Start, State.Deleting, ActivityRefusalKind.Deleting },
        { Act.ViewStep, State.Idle, null },
        { Act.ViewStep, State.Busy, ActivityRefusalKind.Busy },
        { Act.ViewStep, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.ViewStep, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.ViewStep, State.Deleting, ActivityRefusalKind.Deleting },
        { Act.StartRestore, State.Idle, null },
        { Act.StartRestore, State.Busy, ActivityRefusalKind.Busy },
        { Act.StartRestore, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.StartRestore, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.StartRestore, State.Deleting, ActivityRefusalKind.Deleting },
        { Act.PlanEdit, State.Idle, null },
        { Act.PlanEdit, State.Busy, null },
        { Act.PlanEdit, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.PlanEdit, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.PlanEdit, State.Deleting, ActivityRefusalKind.Deleting },
        { Act.StartDelete, State.Idle, null },
        { Act.StartDelete, State.Busy, ActivityRefusalKind.Busy },
        { Act.StartDelete, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.StartDelete, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.StartDelete, State.Deleting, ActivityRefusalKind.Deleting },
    };

    [Theory]
    [MemberData(nameof(Policy))]
    public void Each_action_in_each_state_is_allowed_or_refused_per_the_policy_table(
        Act action, State state, ActivityRefusalKind? expected)
    {
        var mark = _activity.Mark(Folder);
        Enter(state);

        var refusal = Ask(action, mark);

        Assert.Equal(expected, refusal?.Kind);
        if (expected == ActivityRefusalKind.Busy)
            Assert.Equal(BusyReason, refusal!.Reason);
        if (expected == ActivityRefusalKind.Restoring)
            Assert.Equal(ProjectActivity.RestoringReason, refusal!.Reason);
        if (expected == ActivityRefusalKind.Deleting)
            Assert.Equal(ProjectActivity.DeletingReason, refusal!.Reason);
    }

    [Theory]
    [MemberData(nameof(Policy))]
    public void Another_projects_state_never_refuses(Act action, State state, ActivityRefusalKind? _)
    {
        var mark = _activity.Mark(Other);
        Enter(state);
        _busy.BusyFolder = Folder;

        Assert.Null(Ask(action, mark, Other));
    }

    [Fact]
    public void A_start_across_projects_is_refused_when_any_of_them_is_restoring()
    {
        Assert.Null(_activity.TryBeginRestore(Folder));

        var refusal = Start(Other, Folder, Other);

        Assert.Equal(ActivityRefusalKind.Restoring, refusal?.Kind);
        Assert.Equal(ProjectActivity.RestoringReason, refusal!.Reason);
    }

    [Fact]
    public void A_start_across_projects_is_allowed_when_none_is_restoring()
    {
        _busy.Answer = Busy();

        Assert.Null(Start(Other, Folder));
        Assert.Null(Start());
    }

    [Fact]
    public void A_restore_refused_as_busy_releases_the_flag_so_a_later_one_begins()
    {
        _busy.Answer = Busy();

        Assert.Equal(ActivityRefusalKind.Busy, _activity.TryBeginRestore(Folder)?.Kind);
        Assert.False(_activity.IsRestoring(Folder));
        Assert.Null(_activity.MayWrite(Folder));

        _busy.Answer = null;
        Assert.Null(_activity.TryBeginRestore(Folder));
        Assert.True(_activity.IsRestoring(Folder));
    }

    [Fact]
    public void A_restore_claims_the_flag_before_asking_about_busy()
    {
        _busy.OnAsked = () => Assert.True(_activity.IsRestoring(Folder));

        Assert.Null(_activity.TryBeginRestore(Folder));
        Assert.True(_busy.Asked);
    }

    [Fact]
    public void A_second_restore_while_one_runs_is_refused_as_restoring()
    {
        Assert.Null(_activity.TryBeginRestore(Folder));

        Assert.Equal(ActivityRefusalKind.Restoring, _activity.TryBeginRestore(Folder)?.Kind);
        Assert.True(_activity.IsRestoring(Folder));
    }

    [Fact]
    public void Planning_is_refused_when_a_restore_began_and_ended_since_the_mark()
    {
        var mark = _activity.Mark(Folder);
        Assert.Null(_activity.TryBeginRestore(Folder));
        _activity.EndRestore(Folder);

        Assert.False(_activity.IsRestoring(Folder));
        Assert.Equal(ActivityRefusalKind.Restoring, _activity.MayPlanEdit(mark)?.Kind);
        Assert.Null(_activity.MayPlanEdit(_activity.Mark(Folder)));
    }

    [Fact]
    public void Planning_is_allowed_when_the_only_restore_since_the_mark_was_refused_as_busy()
    {
        var mark = _activity.Mark(Folder);
        _busy.Answer = Busy();
        Assert.Equal(ActivityRefusalKind.Busy, _activity.TryBeginRestore(Folder)?.Kind);
        _busy.Answer = null;

        Assert.Null(_activity.MayPlanEdit(mark));
    }

    [Fact]
    public void Writes_are_allowed_again_once_the_restore_ends()
    {
        Assert.Null(_activity.TryBeginRestore(Folder));
        _activity.EndRestore(Folder);

        Assert.Null(_activity.MayWrite(Folder));
        Assert.Null(Start(Folder));
        Assert.Null(_activity.MayViewStep(Folder));
    }

    [Fact]
    public void A_delete_refused_as_busy_releases_the_claim_so_a_later_one_begins()
    {
        _busy.Answer = Busy();

        Assert.Equal(ActivityRefusalKind.Busy, _activity.TryBeginDelete(Folder)?.Kind);
        Assert.Null(_activity.MayWrite(Folder));
        Assert.Null(Start(Folder));

        _busy.Answer = null;
        Assert.Null(_activity.TryBeginDelete(Folder));
        Assert.Equal(ActivityRefusalKind.Deleting, _activity.MayWrite(Folder)?.Kind);
    }

    [Fact]
    public void A_delete_claims_before_asking_about_busy()
    {
        _busy.OnAsked = () => Assert.Equal(ActivityRefusalKind.Deleting, _activity.MayWrite(Folder)?.Kind);

        Assert.Null(_activity.TryBeginDelete(Folder));
        Assert.True(_busy.Asked);
    }

    [Fact]
    public void Deleting_is_not_reported_as_restoring_and_holds_no_restore_waiters()
    {
        Assert.Null(_activity.TryBeginDelete(Folder));

        Assert.False(_activity.IsRestoring(Folder));
        Assert.True(_activity.WhenRestoreEnded(Folder).IsCompleted);
    }

    [Fact]
    public void Ending_a_restore_never_releases_a_delete_nor_the_other_way_round()
    {
        Assert.Null(_activity.TryBeginDelete(Folder));
        _activity.EndRestore(Folder);
        Assert.Equal(ActivityRefusalKind.Deleting, _activity.MayWrite(Folder)?.Kind);
        _activity.EndDelete(Folder);

        Assert.Null(_activity.TryBeginRestore(Folder));
        _activity.EndDelete(Folder);
        Assert.Equal(ActivityRefusalKind.Restoring, _activity.MayWrite(Folder)?.Kind);
    }

    [Fact]
    public void Everything_is_allowed_again_once_the_delete_ends()
    {
        Assert.Null(_activity.TryBeginDelete(Folder));
        _activity.EndDelete(Folder);

        Assert.Null(_activity.MayWrite(Folder));
        Assert.Null(Start(Folder));
        Assert.Null(_activity.MayViewStep(Folder));
        Assert.Null(_activity.MayPlanEdit(_activity.Mark(Folder)));
    }

    [Fact]
    public void WhenRestoreEnded_is_complete_when_no_restore_runs()
    {
        Assert.True(_activity.WhenRestoreEnded(Folder).IsCompleted);
    }

    [Fact]
    public async Task WhenRestoreEnded_completes_on_EndRestore()
    {
        Assert.Null(_activity.TryBeginRestore(Folder));
        var ended = _activity.WhenRestoreEnded(Folder);
        Assert.False(ended.IsCompleted);

        _activity.EndRestore(Folder);

        await ended.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // ── The start protocol (#359) ─────────────────────────────────────────

    [Fact]
    public void A_start_the_gate_allows_runs_and_answers_its_result()
    {
        var start = _activity.TryStart(Folder, () => 42);

        Assert.Equal(new ActivityStart<int>.Started(42), start);
    }

    [Theory]
    [InlineData(State.Restoring, ActivityRefusalKind.Restoring)]
    [InlineData(State.Deleting, ActivityRefusalKind.Deleting)]
    public void A_refused_start_never_runs(State state, ActivityRefusalKind expected)
    {
        Enter(state);
        var ran = false;

        var start = _activity.TryStart(Folder, () => ran = true);

        Assert.Equal(expected, Assert.IsType<ActivityStart<bool>.Refused>(start).Refusal.Kind);
        Assert.False(ran);
    }

    [Fact]
    public void A_start_across_projects_refused_for_one_runs_for_none()
    {
        Assert.Null(_activity.TryBeginDelete(Other));
        var ran = false;

        Assert.Equal(ActivityRefusalKind.Deleting, _activity.TryStart([Folder, Other], () => { ran = true; })?.Kind);
        Assert.False(ran);
    }

    [Fact]
    public void A_restore_after_a_start_finds_the_started_work_busy()
    {
        Assert.Null(_activity.TryStart([Folder], () => { _busy.Answer = Busy(); }));

        Assert.Equal(ActivityRefusalKind.Busy, _activity.TryBeginRestore(Folder)?.Kind);
        Assert.Equal(ActivityRefusalKind.Busy, _activity.TryBeginDelete(Folder)?.Kind);
    }

    [Theory]
    [InlineData(Act.StartRestore)]
    [InlineData(Act.StartDelete)]
    public async Task A_claim_during_a_start_waits_for_it_and_then_finds_the_work_busy(Act claim)
    {
        var ct = TestContext.Current.CancellationToken;
        using var inStart = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var starting = Task.Run(() => _activity.TryStart([Folder], () =>
        {
            inStart.Set();
            finish.Wait(ct);
            _busy.Answer = Busy();
        }), ct);
        Assert.True(inStart.Wait(TimeSpan.FromSeconds(5), ct));

        var claiming = Task.Run(() => Ask(claim, _activity.Mark(Folder)), ct);
        await Task.Delay(100, ct);
        Assert.False(claiming.IsCompleted, "the claim must wait for the start in flight");

        finish.Set();
        Assert.Null(await starting.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(ActivityRefusalKind.Busy, (await claiming.WaitAsync(TimeSpan.FromSeconds(5), ct))?.Kind);
        Assert.False(_activity.IsRestoring(Folder));
        Assert.Null(_activity.MayWrite(Folder));
    }

    [Theory]
    [InlineData(Act.StartRestore, ActivityRefusalKind.Restoring)]
    [InlineData(Act.StartDelete, ActivityRefusalKind.Deleting)]
    public async Task A_start_during_a_claim_waits_for_it_and_then_is_refused(Act claim, ActivityRefusalKind expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var asking = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        _busy.OnAsked = () => { asking.Set(); answer.Wait(ct); };
        var claiming = Task.Run(() => Ask(claim, _activity.Mark(Folder)), ct);
        Assert.True(asking.Wait(TimeSpan.FromSeconds(5), ct));
        _busy.OnAsked = null;

        var ran = false;
        var starting = Task.Run(() => _activity.TryStart([Folder], () => { ran = true; }), ct);
        await Task.Delay(100, ct);
        Assert.False(starting.IsCompleted, "the start must wait for the claim in flight");

        answer.Set();
        Assert.Null(await claiming.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(expected, (await starting.WaitAsync(TimeSpan.FromSeconds(5), ct))?.Kind);
        Assert.False(ran);
    }

    // ── The busy answer ───────────────────────────────────────────────────

    [Fact]
    public void Busy_is_null_while_no_registered_source_is_busy()
    {
        Assert.Null(_activity.BusyFor(Folder));
        Assert.Null(new ProjectActivity().BusyFor(Folder));
    }

    [Fact]
    public void Busy_names_every_busy_source_in_kind_order_whatever_the_registration_order()
    {
        var assembly = new FakeBusy { Answer = new BusySource(BusySourceKind.Assembly, "Assembly: preparing") };
        var attribution = new FakeBusy { Answer = new BusySource(BusySourceKind.CharacterQueue, "Attribution: 2 paragraphs left") };
        _activity.Register(assembly);
        _activity.Register(attribution);
        _busy.Answer = Busy();

        var busy = _activity.BusyFor(Folder);

        Assert.Equal(
            [BusySourceKind.CharacterQueue, BusySourceKind.AudioQueue, BusySourceKind.Assembly],
            busy!.Sources.Select(s => s.Kind));
        Assert.Equal($"Attribution: 2 paragraphs left · {BusyReason} · Assembly: preparing", busy.Reason);
    }

    private ActivityRefusal? Start(params ProjectFolderId[] folders) => _activity.TryStart(folders, () => { });

    private void Enter(State state)
    {
        if (state is State.Restoring or State.BusyAndRestoring)
            Assert.Null(_activity.TryBeginRestore(Folder));
        if (state is State.Busy or State.BusyAndRestoring)
            _busy.Answer = Busy();
        if (state is State.Deleting)
            Assert.Null(_activity.TryBeginDelete(Folder));
    }

    private ActivityRefusal? Ask(Act action, RestoreMark mark, ProjectFolderId? folder = null)
    {
        var asked = folder ?? Folder;
        return action switch
        {
            Act.Write => _activity.MayWrite(asked),
            Act.Start => Start(asked),
            Act.ViewStep => _activity.MayViewStep(asked),
            Act.StartRestore => _activity.TryBeginRestore(asked),
            Act.PlanEdit => _activity.MayPlanEdit(mark),
            Act.StartDelete => _activity.TryBeginDelete(asked),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static BusySource Busy() => new(BusySourceKind.AudioQueue, BusyReason);

    private sealed class FakeBusy : IBusySource
    {
        public BusySource? Answer { get; set; }
        /// <summary>When set, only this folder is busy; otherwise the answer is for every folder.</summary>
        public ProjectFolderId? BusyFolder { get; set; }
        public Action? OnAsked { get; set; }
        public bool Asked { get; private set; }

        public BusySource? BusyFor(ProjectFolderId folder)
        {
            Asked = true;
            OnAsked?.Invoke();
            return BusyFolder is { } only && only != folder ? null : Answer;
        }
    }
}
