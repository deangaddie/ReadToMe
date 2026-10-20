using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// Background runs in History over a real migrated project: a run's commits are one step, a gesture
/// that lands during it splits it into parts, and a run that ends early says how (spec, "Step
/// boundaries"). Runs are started and ended here the way the run tracker does it; run work commits
/// from DI scopes that joined the runs, as the queue worker and the voice batch do.
/// </summary>
public class BackgroundRunStepsTests : ProjectDbTestBase
{
    private ServiceProvider _root;
    private readonly ProjectFolderId _folder;

    public BackgroundRunStepsTests()
    {
        _root = BuildRoot();
        _folder = new ProjectFolderId(FolderName);
    }

    private ProjectHistory History => _root.GetRequiredService<ProjectHistory>();
    private BackgroundRuns Runs => _root.GetRequiredService<BackgroundRuns>();

    [Fact]
    public async Task AnAttributionRun_IsOneClosedStep_LabelledForTheRun_WithATimeSpan()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        Assert.True((await History.ListAsync(_folder)).Steps[0].IsOpen);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution", "History started"], steps.Select(s => s.Label));
        var run = steps[0];
        Assert.False(run.IsOpen);
        Assert.Null(run.Note);
        Assert.NotNull(run.EndedAt);
        Assert.True(run.StartedAt <= run.EndedAt);
    }

    [Fact]
    public async Task AUserEditDuringARun_SplitsTheRun_IntoPart1_TheEdit_AndPart2()
    {
        var b = await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await InScopeAsync("Edit text", m => m.CommitAsync(new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Edited.")));
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution", "Edit text", "Attribution", "History started"], steps.Select(s => s.Label));
        Assert.Equal(["part 2", null, "part 1", null], steps.Select(s => s.Note));
        Assert.All(steps, s => Assert.False(s.IsOpen));
    }

    [Fact]
    public async Task ARunCommitDuringAMultiCommitRequest_SplitsTheRequest_IntoPart1_TheRunsCommit_AndPart2()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await InScopeAsync("Discover characters", async m =>
        {
            await m.CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
            await RunCommitAsync(new CreateCharacterMutation(_folder, "Mallory"));
            await m.CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        });
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(
            ["Discover characters", "Attribution", "Discover characters", "History started"], steps.Select(s => s.Label));
        Assert.Equal(["part 2", null, "part 1", null], steps.Select(s => s.Note));
    }

    [Fact]
    public async Task ARequestThatIsNotInterrupted_HasNoNote()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        await InScopeAsync("New character", async m =>
        {
            await m.CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
            await m.CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        });

        Assert.Null((await History.ListAsync(_folder)).Steps[0].Note);
    }

    [Fact]
    public async Task AttributionAndAVoiceBatchThatOverlap_ShareOneStep_LabelledWithBoth()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"), BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"), BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Carol"), BackgroundRunKind.VoiceBatch);
        Assert.True((await History.ListAsync(_folder)).Steps[0].IsOpen); // the voice batch keeps it open
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution and voice batch", "History started"], steps.Select(s => s.Label));
        Assert.False(steps[0].IsOpen);
    }

    [Fact]
    public async Task ARunThatJoinsButCommitsNothing_IsNotInTheLabel()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"), BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        Assert.Equal("Attribution", (await History.ListAsync(_folder)).Steps[0].Label);
    }

    [Fact]
    public async Task EachPartOfARun_IsLabelledFromTheWorkThatLandedInIt()
    {
        var b = await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"), BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"), BackgroundRunKind.VoiceBatch);
        await InScopeAsync("Edit text", m => m.CommitAsync(new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Edited.")));
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Carol"), BackgroundRunKind.VoiceBatch);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(
            ["Voice batch", "Edit text", "Attribution and voice batch", "History started"], steps.Select(s => s.Label));
    }

    [Fact]
    public async Task ACancelledRun_ClosesWithHowItEnded()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution, "212 of 340 paragraphs, cancelled");

        var run = (await History.ListAsync(_folder)).Steps[0];
        Assert.False(run.IsOpen);
        Assert.Equal("212 of 340 paragraphs, cancelled", run.Note);
    }

    [Fact]
    public async Task AFailedRunInItsSecondPart_SaysBoth()
    {
        var b = await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await InScopeAsync("Edit text", m => m.CommitAsync(new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Edited.")));
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch, "3 of 7 voices, failed");

        Assert.Equal("part 2 · 3 of 7 voices, failed", (await History.ListAsync(_folder)).Steps[0].Note);
    }

    [Fact]
    public async Task ARunThatChangedNothing_LeavesNoStep()
    {
        var b = await SeedOneItemAsync();
        var before = await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new SetParagraphItemAudioMutation(_folder, b.ItemId("item"), "audio/take.wav")); // a result
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution, "0 of 3 paragraphs, cancelled");

        Assert.Equal(before.Steps, (await History.ListAsync(_folder)).Steps);
    }

    [Fact]
    public async Task ResultsCommittedDuringARun_DoNotSplitIt()
    {
        var b = await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await CommitAsync(new SetParagraphItemAudioMutation(_folder, b.ItemId("item"), "audio/take.wav"));
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution", "History started"], steps.Select(s => s.Label));
        Assert.Null(steps[0].Note);
    }

    [Fact]
    public async Task RunWorkCommittedWithNoRunOpen_IsAStepOfItsOwn()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));

        var step = (await History.ListAsync(_folder)).Steps[0];
        Assert.Equal("Create character", step.Label);
        Assert.False(step.IsOpen);
    }

    [Fact]
    public async Task AnOpenStepsEndTime_IsItsLastChange()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var first = (await History.ListAsync(_folder)).Steps[0];
        await Task.Delay(20);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var second = (await History.ListAsync(_folder)).Steps[0];
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);

        Assert.True(first.IsOpen);
        Assert.Equal(first.StartedAt, first.EndedAt);
        Assert.True(second.EndedAt > first.EndedAt);
    }

    [Fact]
    public async Task EachLaterChunkOfAnOpenStep_AnnouncesItsRowAsItNowStands()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        var announced = new List<HistoryStepEvent>();
        _root.GetRequiredService<EventBroadcaster<HistoryStepEvent>>().Event += announced.Add;

        Runs.Started(_folder, BackgroundRunKind.Attribution);
        Runs.Started(_folder, BackgroundRunKind.VoiceBatch);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"), BackgroundRunKind.Attribution);
        await Task.Delay(20);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"), BackgroundRunKind.VoiceBatch);

        Assert.Equal([HistoryStepEventKind.Landed, HistoryStepEventKind.Changed], announced.Select(e => e.Kind));
        var (landed, changed) = (announced[0].Step, announced[1].Step);
        Assert.Equal(landed.StepId, changed.StepId);
        Assert.True(changed.IsOpen);
        Assert.Equal("Attribution and voice batch", changed.Label);
        Assert.True(changed.EndedAt > landed.EndedAt);
        await Runs.EndedAsync(_folder, BackgroundRunKind.Attribution);
        await Runs.EndedAsync(_folder, BackgroundRunKind.VoiceBatch);
    }

    [Fact]
    public async Task OnAppStart_EveryOpenStepClosesAsInterrupted()
    {
        var b = await SeedOneItemAsync();
        await History.ListAsync(_folder);
        Runs.Started(_folder, BackgroundRunKind.Attribution);
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await InScopeAsync("Edit text", m => m.CommitAsync(new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Edited.")));
        await RunCommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var lastChange = (await History.ListAsync(_folder)).Steps[0];
        Assert.True(lastChange.IsOpen);

        // The app stops with the run open, and starts again.
        await _root.DisposeAsync();
        _root = BuildRoot();
        await History.CloseInterruptedAsync(_folder);

        var run = (await History.ListAsync(_folder)).Steps[0];
        Assert.Equal("Attribution", run.Label);
        Assert.False(run.IsOpen);
        Assert.Equal("part 2 · interrupted", run.Note);
        Assert.Equal(lastChange.EndedAt, run.EndedAt); // when it really ended is not known; its last change is
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ServiceProvider BuildRoot()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        return services.BuildServiceProvider();
    }

    /// <summary>Run work: a DI scope that joined the background runs, as the queue worker's and the voice batch's do.</summary>
    private async Task<BookMutationOutcome> RunCommitAsync(
        BookMutation mutation, BackgroundRunKind kind = BackgroundRunKind.Attribution)
    {
        await using var scope = _root.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HistoryStepScope>().JoinBackgroundRuns(kind);
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    /// <summary>A commit from no scope at all.</summary>
    private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    /// <summary>One request: a DI scope with a step scope open across everything <paramref name="body"/> commits.</summary>
    private async Task InScopeAsync(string label, Func<BookMutations, Task> body)
    {
        await using var scope = _root.CreateAsyncScope();
        var steps = scope.ServiceProvider.GetRequiredService<HistoryStepScope>();
        await using (steps.Open(_folder, label))
            await body(scope.ServiceProvider.GetRequiredService<BookMutations>());
    }

    private async Task<BookHierarchyBuilder> SeedOneItemAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "Hello world"))))
            .BuildAsync();
        return b;
    }

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
