using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// The <b>Project activity</b> gate's policy table (spec #112, "Policy"): every action in every state
/// — idle, busy, restoring, and both — through the gate's public questions, with busy faked.
/// </summary>
public class ProjectActivityTests
{
    private static readonly ProjectFolderId Folder = new("Book");
    private static readonly ProjectFolderId Other = new("Other");
    private const string BusyReason = "Audio Queue: 3 paragraphs";

    private readonly FakeBusy _busy = new();
    private readonly ProjectActivity _activity;

    public ProjectActivityTests() => _activity = new ProjectActivity(() => _busy);

    public enum State { Idle, Busy, Restoring, BusyAndRestoring }

    public enum Act { Write, Enqueue, ViewStep, StartRestore, PlanEdit }

    public static TheoryData<Act, State, ActivityRefusalKind?> Policy => new()
    {
        { Act.Write, State.Idle, null },
        { Act.Write, State.Busy, null },
        { Act.Write, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.Write, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.Enqueue, State.Idle, null },
        { Act.Enqueue, State.Busy, null },
        { Act.Enqueue, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.Enqueue, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.ViewStep, State.Idle, null },
        { Act.ViewStep, State.Busy, ActivityRefusalKind.Busy },
        { Act.ViewStep, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.ViewStep, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.StartRestore, State.Idle, null },
        { Act.StartRestore, State.Busy, ActivityRefusalKind.Busy },
        { Act.StartRestore, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.StartRestore, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
        { Act.PlanEdit, State.Idle, null },
        { Act.PlanEdit, State.Busy, null },
        { Act.PlanEdit, State.Restoring, ActivityRefusalKind.Restoring },
        { Act.PlanEdit, State.BusyAndRestoring, ActivityRefusalKind.Restoring },
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
        Assert.Null(_activity.MayEnqueue(Folder));
        Assert.Null(_activity.MayViewStep(Folder));
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

    private void Enter(State state)
    {
        if (state is State.Restoring or State.BusyAndRestoring)
            Assert.Null(_activity.TryBeginRestore(Folder));
        if (state is State.Busy or State.BusyAndRestoring)
            _busy.Answer = Busy();
    }

    private ActivityRefusal? Ask(Act action, RestoreMark mark, ProjectFolderId? folder = null)
    {
        var asked = folder ?? Folder;
        return action switch
        {
            Act.Write => _activity.MayWrite(asked),
            Act.Enqueue => _activity.MayEnqueue(asked),
            Act.ViewStep => _activity.MayViewStep(asked),
            Act.StartRestore => _activity.TryBeginRestore(asked),
            Act.PlanEdit => _activity.MayPlanEdit(mark),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static ProjectBusy Busy() => new([new BusySource(BusySourceKind.AudioQueue, BusyReason)]);

    private sealed class FakeBusy : IProjectBusyAnswer
    {
        public ProjectBusy? Answer { get; set; }
        /// <summary>When set, only this folder is busy; otherwise the answer is for every folder.</summary>
        public ProjectFolderId? BusyFolder { get; set; }
        public Action? OnAsked { get; set; }
        public bool Asked { get; private set; }

        public ProjectBusy? For(ProjectFolderId folder)
        {
            Asked = true;
            OnAsked?.Invoke();
            return BusyFolder is { } only && only != folder ? null : Answer;
        }
    }
}
