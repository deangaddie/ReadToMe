using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// Recording a commit as History over a real migrated project, driven through the capture itself
/// rather than <see cref="BookMutations"/>: each commit writes rows on the
/// <see cref="ProjectDbContext"/>, records, commits or rolls back, and is then told it committed,
/// the way a Book mutation does it. Covers the step scope's state machine — extend, append, split
/// into parts, close — for requests and Background runs (spec, "Step boundaries").
/// </summary>
public class HistoryRecordingTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private readonly List<AsyncServiceScope> _scopes = [];

    public HistoryRecordingTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
    }

    private ProjectHistory History => _root.GetRequiredService<ProjectHistory>();
    private BackgroundRuns Runs => _root.GetRequiredService<BackgroundRuns>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── steps of their own ──────────────────────────────────────────────────

    [Fact]
    public async Task ACommitNoScopeHolds_IsAClosedStepOfItsOwn_AtTheTip_NamedFromTheMutation()
    {
        await StartedAsync();

        var first = await CommitAsync(Steps(), AddCharacter("Alice"));
        var second = await CommitAsync(Steps(), AddCharacter("Bob"));

        var list = await History.ListAsync(_folder, Ct);
        Assert.Equal(["Create character", "Create character", "History started"], list.Steps.Select(s => s.Label));
        Assert.Equal([3, 2, 1], list.Steps.Select(s => s.Sequence));
        Assert.Equal(list.Steps[1].StepId, list.Steps[0].ParentStepId);
        Assert.Equal(list.Steps[2].StepId, list.Steps[1].ParentStepId);
        Assert.Equal(list.Steps[0].StepId, list.HeadStepId);
        Assert.Equal(list.Steps[1], first);
        Assert.Equal(list.Steps[0], second);

        var tip = list.Steps[0];
        Assert.Equal(HistoryStepKind.Request, tip.Kind); // no scope names it: a request's edit of its own
        Assert.Equal(StepAction.Edit, tip.Action);
        Assert.False(tip.IsOpen);
        Assert.False(string.IsNullOrWhiteSpace(tip.Author));
        Assert.NotNull(tip.EndedAt);
        Assert.True(tip.StartedAt <= tip.EndedAt);
    }

    [Fact]
    public async Task ACommitThatChangesNoState_RecordsNothing_AndIsToldNoStep()
    {
        // AudioReviews and StepCheckpoints are results and progress, not Project state: the session
        // never watches them, so a commit touching only them, or nothing, leaves History as it was.
        var b = await StartedAsync();
        var before = await History.ListAsync(_folder, Ct);

        Assert.Null(await CommitAsync(Steps(), ResultsOnly(b.ItemId("item"))));
        Assert.Null(await CommitAsync(Steps(), WritesNothing()));

        Assert.Equal(before.Steps, (await History.ListAsync(_folder, Ct)).Steps);
    }

    [Fact]
    public async Task ARolledBackCommit_LeavesNoStep_AndTheNextOneStillRecords()
    {
        await StartedAsync();
        var before = await History.ListAsync(_folder, Ct);

        await RollBackAsync(Steps(), AddCharacter("Alice"));
        Assert.Equal(before.Steps, (await History.ListAsync(_folder, Ct)).Steps);

        await CommitAsync(Steps(), AddCharacter("Bob"));
        var after = await History.ListAsync(_folder, Ct);
        Assert.Equal("Create character", after.Steps[0].Label);
        Assert.Equal(before.HeadStepId, after.Steps[0].ParentStepId);
    }

    [Fact]
    public async Task AnImportsScope_RecordsAnImportStep()
    {
        await StartedAsync();

        var steps = Steps();
        await using (steps.OpenImport(_folder, "Import book"))
            await CommitAsync(steps, AddCharacter("Alice"));

        var import = (await History.ListAsync(_folder, Ct)).Steps[0];
        Assert.Equal("Import book", import.Label);
        Assert.Equal(HistoryStepKind.Import, import.Kind);
        Assert.Null(import.Action);
        Assert.False(import.IsOpen);
    }

    // ── a request's step scope ──────────────────────────────────────────────

    [Fact]
    public async Task EverythingCommittedInsideOneScope_IsOneStep_OpenUntilTheScopeCloses()
    {
        await StartedAsync();
        var before = await History.ListAsync(_folder, Ct);
        HistoryStep? landed = null, extended = null;

        await InRequestAsync("New character", async steps =>
        {
            landed = await CommitAsync(steps, AddCharacter("Alice"));
            extended = await CommitAsync(steps, AddCharacter("Bob"));
            Assert.True((await History.ListAsync(_folder, Ct)).Steps[0].IsOpen);
        }, StepAction.Character);

        var list = await History.ListAsync(_folder, Ct);
        Assert.Equal(["New character", "History started"], list.Steps.Select(s => s.Label));
        var step = list.Steps[0];
        Assert.Equal(HistoryStepKind.Request, step.Kind);
        Assert.Equal(StepAction.Character, step.Action);
        Assert.False(step.IsOpen);
        Assert.Null(step.Note); // never interrupted
        Assert.NotNull(step.EndedAt);
        Assert.Equal(before.HeadStepId, step.ParentStepId);
        Assert.Equal(step.StepId, list.HeadStepId);
        Assert.Equal(step.StepId, landed!.StepId);
        Assert.Equal(step.StepId, extended!.StepId);
        Assert.Equal(("New character", StepAction.Character), (extended.Label, extended.Action));
    }

    [Fact]
    public async Task AScopeThatCommitsNothing_RecordsNothing()
    {
        await StartedAsync();
        var before = await History.ListAsync(_folder, Ct);

        await InRequestAsync("New character", steps => CommitAsync(steps, WritesNothing()));

        Assert.Equal(before.Steps, (await History.ListAsync(_folder, Ct)).Steps);
    }

    [Fact]
    public async Task ARolledBackChunk_DoesNotCountTowardTheScopesStep()
    {
        await StartedAsync();

        await InRequestAsync("New character", async steps =>
        {
            await RollBackAsync(steps, AddCharacter("Alice"));
            await CommitAsync(steps, AddCharacter("Bob"));
            await RollBackAsync(steps, AddCharacter("Carol"));
            await CommitAsync(steps, AddCharacter("Dave"));
        });

        var steps = (await History.ListAsync(_folder, Ct)).Steps;
        Assert.Equal(["New character", "History started"], steps.Select(s => s.Label));
        Assert.Null(steps[0].Note); // the scope never moved onto a step that rolled back, so it was never split
    }

    [Fact]
    public async Task ACaptureNeverToldItCommitted_AnnouncesNothing()
    {
        await StartedAsync();
        var announced = Listen();

        var capture = await RecordAsync(Steps(), AddCharacter("Alice"), commit: false);
        capture.Announce();

        Assert.Empty(announced);
    }

    [Fact]
    public async Task ACommitFromOutsideTheScope_SplitsIt_IntoPart1_ThatCommitsOwnStep_AndPart2()
    {
        await StartedAsync();

        await InRequestAsync("Discover characters", async steps =>
        {
            await CommitAsync(steps, AddCharacter("Alice"));
            await CommitAsync(Steps(), AddCharacter("Mallory")); // another request, no scope
            await CommitAsync(steps, AddCharacter("Bob"));
        });

        var list = await History.ListAsync(_folder, Ct);
        Assert.Equal(
            ["Discover characters", "Create character", "Discover characters", "History started"],
            list.Steps.Select(s => s.Label));
        Assert.Equal(["part 2", null, "part 1", null], list.Steps.Select(s => s.Note));
        Assert.All(list.Steps, s => Assert.False(s.IsOpen));
        Assert.Equal(list.Steps[1].StepId, list.Steps[0].ParentStepId);
    }

    [Fact]
    public async Task AStepAcrossChapters_SaysHowManyChapters_AndAddsUpEveryCommitInTheScope()
    {
        var b = await SeedTwoChaptersAsync();

        await InRequestAsync("Edit text", async steps =>
        {
            await CommitAsync(steps, EditItem(b.ItemId("a1"), "One."));
            await CommitAsync(steps, EditItem(b.ItemId("b1"), "Two."));
        });

        Assert.Equal("2 paragraphs across 2 chapters", (await History.ListAsync(_folder, Ct)).Steps[0].Summary);
    }

    [Fact]
    public async Task AScopeCarryingOnInANewStep_DescribesOnlyWhatThatStepChanged()
    {
        var b = await SeedTwoChaptersAsync();

        await InRequestAsync("Edit text", async steps =>
        {
            await CommitAsync(steps, EditItem(b.ItemId("b1"), "One."));
            await CommitAsync(Steps(), AddCharacter("Mallory")); // another request, no scope
            await CommitAsync(steps, EditItem(b.ItemId("a1"), "Two."));
        });

        var steps = (await History.ListAsync(_folder, Ct)).Steps;
        Assert.Equal("1 paragraph in Chapter 3", steps[0].Summary);
        Assert.Equal("1 paragraph in Chapter 4", steps[2].Summary);
    }

    // ── announcing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AStepOfItsOwn_IsAnnouncedAsLandedThenClosed_WithItsRow()
    {
        await StartedAsync();
        var announced = Listen();

        await CommitAsync(Steps(), AddCharacter("Alice"));

        var tip = (await History.ListAsync(_folder, Ct)).Steps[0];
        Assert.Equal([HistoryStepEventKind.Landed, HistoryStepEventKind.Closed], announced.Select(e => e.Kind));
        Assert.All(announced, e => Assert.Equal(_folder, e.FolderId));
        Assert.All(announced, e => Assert.Equal(tip, e.Step));
    }

    [Fact]
    public async Task AScopedStep_IsAnnouncedLandedOpen_ChangedByEachLaterCommit_ThenClosedWhenTheScopeEnds()
    {
        await StartedAsync();
        var announced = Listen();

        await InRequestAsync("New character", async steps =>
        {
            await CommitAsync(steps, AddCharacter("Alice"));
            await CommitAsync(steps, AddCharacter("Bob"));
        });

        Assert.Equal(
            [HistoryStepEventKind.Landed, HistoryStepEventKind.Changed, HistoryStepEventKind.Closed],
            announced.Select(e => e.Kind));
        Assert.True(announced[0].Step.IsOpen);
        Assert.True(announced[1].Step.IsOpen);
        Assert.Equal((await History.ListAsync(_folder, Ct)).Steps[0], announced[2].Step);
    }

    // ── Background runs ─────────────────────────────────────────────────────

    [Fact]
    public async Task AnAttributionRun_IsOneClosedStep_LabelledForTheRun_WithATimeSpan()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(AddCharacter("Alice"));
        await RunCommitAsync(AddCharacter("Bob"));
        Assert.True((await History.ListAsync(_folder, Ct)).Steps[0].IsOpen);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        var steps = (await History.ListAsync(_folder, Ct)).Steps;
        Assert.Equal(["Attribution", "History started"], steps.Select(s => s.Label));
        var run = steps[0];
        Assert.Equal(HistoryStepKind.BackgroundRun, run.Kind);
        Assert.Equal(StepAction.Attribution, run.Action);
        Assert.False(run.IsOpen);
        Assert.Null(run.Note);
        Assert.NotNull(run.EndedAt);
        Assert.True(run.StartedAt <= run.EndedAt);
    }

    [Fact]
    public async Task ARunCommitDuringAMultiCommitRequest_SplitsTheRequest_IntoPart1_TheRunsCommit_AndPart2()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await InRequestAsync("Discover characters", async steps =>
        {
            await CommitAsync(steps, AddCharacter("Alice"));
            await RunCommitAsync(AddCharacter("Mallory"));
            await CommitAsync(steps, AddCharacter("Bob"));
        });
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        var steps = (await History.ListAsync(_folder, Ct)).Steps;
        Assert.Equal(
            ["Discover characters", "Attribution", "Discover characters", "History started"], steps.Select(s => s.Label));
        Assert.Equal(["part 2", null, "part 1", null], steps.Select(s => s.Note));
    }

    [Fact]
    public async Task AttributionAndAVoiceBatchThatOverlap_ShareOneStep_LabelledWithBoth()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(AddCharacter("Alice"), BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(AddCharacter("Bob"), BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(AddCharacter("Carol"), BackgroundRunKind.VoiceBatch);
        Assert.True((await History.ListAsync(_folder, Ct)).Steps[0].IsOpen); // the voice batch keeps it open
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);

        var steps = (await History.ListAsync(_folder, Ct)).Steps;
        Assert.Equal(["Attribution and voice batch", "History started"], steps.Select(s => s.Label));
        Assert.False(steps[0].IsOpen);
    }

    [Fact]
    public async Task ARunThatJoinsButCommitsNothing_IsNotInTheLabel()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(AddCharacter("Alice"), BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        Assert.Equal("Attribution", (await History.ListAsync(_folder, Ct)).Steps[0].Label);
    }

    [Fact]
    public async Task EachPartOfARun_IsLabelledFromTheWorkThatLandedInIt()
    {
        var b = await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(AddCharacter("Alice"), BackgroundRunKind.Attribution);
        await RunCommitAsync(AddCharacter("Bob"), BackgroundRunKind.VoiceBatch);
        await InRequestAsync("Edit text", steps => CommitAsync(steps, EditItem(b.ItemId("item"), "Edited.")));
        await RunCommitAsync(AddCharacter("Carol"), BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);

        var steps = (await History.ListAsync(_folder, Ct)).Steps;
        Assert.Equal(
            ["Voice batch", "Edit text", "Attribution and voice batch", "History started"], steps.Select(s => s.Label));
        Assert.Equal(
            [HistoryStepKind.BackgroundRun, HistoryStepKind.Request, HistoryStepKind.BackgroundRun, HistoryStepKind.Started],
            steps.Select(s => s.Kind));
        Assert.Equal([StepAction.Voice, StepAction.Edit, StepAction.Attribution, null], steps.Select(s => s.Action));
    }

    [Fact]
    public async Task ARunsAction_FollowsTheWorkThatLandsInIt_AndTheCommitIsToldTheStepAsItNowStands()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        var first = await RunCommitAsync(AddCharacter("Alice"), BackgroundRunKind.VoiceBatch);
        Runs.Started(_folder, BackgroundRunKind.Attribution);
        var second = await RunCommitAsync(AddCharacter("Bob"), BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        Assert.Equal(("Voice batch", StepAction.Voice), (first!.Label, first.Action));
        Assert.Equal(("Attribution and voice batch", StepAction.Attribution), (second!.Label, second.Action));
        var run = (await History.ListAsync(_folder, Ct)).Steps[0];
        Assert.Equal("Attribution and voice batch", run.Label);
        Assert.Equal(StepAction.Attribution, run.Action);
    }

    [Fact]
    public async Task ACancelledRun_ClosesWithHowItEnded()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(AddCharacter("Alice"));
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution, "212 of 340 paragraphs, cancelled");

        var run = (await History.ListAsync(_folder, Ct)).Steps[0];
        Assert.False(run.IsOpen);
        Assert.Equal("212 of 340 paragraphs, cancelled", run.Note);
    }

    [Fact]
    public async Task AFailedRunInItsSecondPart_SaysBoth()
    {
        var b = await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(AddCharacter("Alice"));
        await InRequestAsync("Edit text", steps => CommitAsync(steps, EditItem(b.ItemId("item"), "Edited.")));
        await RunCommitAsync(AddCharacter("Bob"));
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch, "3 of 7 voices, failed");

        Assert.Equal("part 2 · 3 of 7 voices, failed", (await History.ListAsync(_folder, Ct)).Steps[0].Note);
    }

    [Fact]
    public async Task RunWorkCommittedWithNoRunOpen_IsAStepOfItsOwn()
    {
        await StartedAsync();

        await RunCommitAsync(AddCharacter("Alice"));

        var step = (await History.ListAsync(_folder, Ct)).Steps[0];
        Assert.Equal("Create character", step.Label);
        Assert.False(step.IsOpen);
    }

    [Fact]
    public async Task AnOpenStepsEndTime_IsItsLastChange()
    {
        await StartedAsync();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(AddCharacter("Alice"));
        var first = (await History.ListAsync(_folder, Ct)).Steps[0];
        await Task.Delay(20, Ct);
        await RunCommitAsync(AddCharacter("Bob"));
        var second = (await History.ListAsync(_folder, Ct)).Steps[0];
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        Assert.True(first.IsOpen);
        Assert.Equal(first.StartedAt, first.EndedAt);
        Assert.True(second.EndedAt > first.EndedAt);
    }

    [Fact]
    public async Task EachLaterChunkOfAnOpenStep_AnnouncesItsRowAsItNowStands()
    {
        await StartedAsync();
        var announced = Listen();

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(AddCharacter("Alice"), BackgroundRunKind.Attribution);
        await Task.Delay(20, Ct);
        await RunCommitAsync(AddCharacter("Bob"), BackgroundRunKind.VoiceBatch);

        Assert.Equal([HistoryStepEventKind.Landed, HistoryStepEventKind.Changed], announced.Select(e => e.Kind));
        var (landed, changed) = (announced[0].Step, announced[1].Step);
        Assert.Equal(landed.StepId, changed.StepId);
        Assert.True(changed.IsOpen);
        Assert.Equal("Attribution and voice batch", changed.Label);
        Assert.True(changed.EndedAt > landed.EndedAt);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>A state commit: the mutation it stands for, and the rows it writes, answering what it changed.</summary>
    private sealed record Write(ProjectStateMutation Mutation, Func<ProjectDbContext, Task<BookMutationEffects>> Apply);

    private Write AddCharacter(string name) => new(new CreateCharacterMutation(_folder, name), db =>
    {
        db.Characters.Add(new Character { Id = Guid.NewGuid(), Name = name });
        return Task.FromResult(new BookMutationEffects { Scope = BookMutationScope.Exact, Facets = BookFacets.Characters });
    });

    private Write EditItem(Guid itemId, string text) => new(new UpdateParagraphItemTextMutation(_folder, itemId, text), async db =>
    {
        var item = (await db.ParagraphItems.FindAsync([itemId], Ct))!;
        item.Text = text;
        return new BookMutationEffects
        {
            Scope = BookMutationScope.Exact, Facets = BookFacets.ItemText, ParagraphIds = [item.ParagraphId], ParagraphItemIds = [item.Id],
        };
    });

    /// <summary>Rows only in the result tables, which are not Project state.</summary>
    private Write ResultsOnly(Guid itemId) => new(new CreateCharacterMutation(_folder, "unused"), db =>
    {
        db.AudioReviews.Add(new AudioReview { ParagraphItemId = itemId });
        db.StepCheckpoints.Add(new StepCheckpoint { Step = PipelineStep.Speakers, State = CheckpointState.Confirmed, At = DateTimeOffset.UnixEpoch });
        return Task.FromResult(new BookMutationEffects { Scope = BookMutationScope.WholeProject, Facets = BookFacets.Checkpoints });
    });

    private Write WritesNothing() => new(new CreateCharacterMutation(_folder, "unused"),
        _ => Task.FromResult(new BookMutationEffects { Scope = BookMutationScope.Exact, Facets = BookFacets.Characters }));

    /// <summary>
    /// One commit, as a Book mutation makes it: recorded and committed, then told it committed
    /// (under the lock, in the app) and announced (after it). Answers the step it was told.
    /// </summary>
    private async Task<HistoryStep?> CommitAsync(HistoryStepScope steps, Write write)
    {
        var capture = await RecordAsync(steps, write, commit: true);
        var step = await capture.CommittedAsync();
        capture.Announce();
        return step;
    }

    /// <summary>One commit that rolls back after recording: never told it committed.</summary>
    private Task RollBackAsync(HistoryStepScope steps, Write write) => RecordAsync(steps, write, commit: false);

    private async Task<HistoryCapture> RecordAsync(HistoryStepScope steps, Write write, bool commit)
    {
        await using var db = await OpenDbAsync();
        var capture = await steps.BeginCaptureAsync(_folder, db, Ct);
        await using (capture)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            var effects = await write.Apply(db);
            await db.SaveChangesAsync(Ct);
            await capture.RecordAsync(write.Mutation, effects, Ct);
            if (commit)
                await transaction.CommitAsync(Ct);
            else
                await transaction.RollbackAsync(Ct);
        }
        return capture;
    }

    /// <summary>A DI scope's step scope with nothing open: its commits are steps of their own.</summary>
    private HistoryStepScope Steps()
    {
        var scope = _root.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<HistoryStepScope>();
    }

    /// <summary>One request: a step scope open across everything <paramref name="body"/> commits through it.</summary>
    private async Task InRequestAsync(string label, Func<HistoryStepScope, Task> body, StepAction action = StepAction.Edit)
    {
        var steps = Steps();
        await using (steps.Open(_folder, label, action))
            await body(steps);
    }

    /// <summary>Run work: a DI scope that joined the Background runs, as the queue worker's and the voice batch's do.</summary>
    private Task<HistoryStep?> RunCommitAsync(Write write, BackgroundRunKind kind = BackgroundRunKind.Attribution)
    {
        var steps = Steps();
        steps.JoinBackgroundRuns(kind);
        return CommitAsync(steps, write);
    }

    private List<HistoryStepEvent> Listen()
    {
        var announced = new List<HistoryStepEvent>();
        _root.GetRequiredService<EventBroadcaster<HistoryStepEvent>>().Event += announced.Add;
        return announced;
    }

    /// <summary>One item in one chapter, with History started.</summary>
    private async Task<BookHierarchyBuilder> StartedAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "Hello world"))))
            .BuildAsync();
        await History.ListAsync(_folder, Ct);
        return b;
    }

    /// <summary>"Chapter 3" with two paragraphs, "Chapter 4" with one.</summary>
    private async Task<BookHierarchyBuilder> SeedTwoChaptersAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v
                .AddChapter("Chapter 3", c => c
                    .AddParagraph("pa1", p => p.AddNarration("a1", "First."))
                    .AddParagraph("pa2", p => p.AddNarration("a2", "Second.")))
                .AddChapter("Chapter 4", c => c
                    .AddParagraph("pb1", p => p.AddNarration("b1", "Third."))))
            .BuildAsync();
        return b;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
            await scope.DisposeAsync();
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
