using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.Mutations;

/// <summary>
/// Confirming, skipping and reopening a Pipeline step, proved through
/// <see cref="BookMutations.CommitAsync"/> against a real SQLite project.
/// <para>
/// A gesture that leaves the row as it was must be <see cref="BookMutationOutcome.NoChange"/>: the
/// pill the user clicked already shows that state, and a receipt would refetch every open Overview
/// for nothing.
/// </para>
/// </summary>
public class CheckpointMutationTests : ProjectDbTestBase
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;

    public CheckpointMutationTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        services.AddSingleton<TimeProvider>(_clock);
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
    }

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Set_onAStepWithNoRow_storesItAndReportsCheckpoints()
    {
        await SeedBookAsync();

        var effects = await AppliedAsync(Set(PipelineStep.Structure, CheckpointState.Confirmed));

        Assert.Equal(BookFacets.Checkpoints, effects.Facets);
        var row = Assert.Single(await CheckpointRowsAsync());
        Assert.Equal((PipelineStep.Structure, CheckpointState.Confirmed, false, Start),
            (row.Step, row.State, row.Stale, row.At));
    }

    [Fact]
    public async Task Set_aDifferentState_replacesTheRow()
    {
        await SeedBookAsync();
        await SeedCheckpointAsync(PipelineStep.Titles, CheckpointState.Confirmed, stale: false, at: Start.AddDays(-1));

        var effects = await AppliedAsync(Set(PipelineStep.Titles, CheckpointState.Skipped));

        Assert.Equal(BookFacets.Checkpoints, effects.Facets);
        var row = Assert.Single(await CheckpointRowsAsync());
        Assert.Equal((CheckpointState.Skipped, false, Start), (row.State, row.Stale, row.At));
    }

    [Fact]
    public async Task Open_deletesTheRow()
    {
        await SeedBookAsync();
        await SeedCheckpointAsync(PipelineStep.Text, CheckpointState.Skipped, stale: true, at: Start.AddDays(-1));

        var effects = await AppliedAsync(Set(PipelineStep.Text, null));

        Assert.Equal(BookFacets.Checkpoints, effects.Facets);
        Assert.Empty(await CheckpointRowsAsync());
    }

    [Fact]
    public async Task Set_theCurrentFreshState_changesNothing()
    {
        await SeedBookAsync();
        await SeedCheckpointAsync(PipelineStep.Structure, CheckpointState.Confirmed, stale: false, at: Start.AddDays(-1));

        Assert.IsType<BookMutationOutcome.NoChange>(
            await CommitAsync(Set(PipelineStep.Structure, CheckpointState.Confirmed)));

        // Re-clicking a settled pill is not a new confirm: the time it was settled stays.
        Assert.Equal(Start.AddDays(-1), Assert.Single(await CheckpointRowsAsync()).At);
    }

    [Fact]
    public async Task Open_withNoRow_changesNothing()
    {
        await SeedBookAsync();

        Assert.IsType<BookMutationOutcome.NoChange>(await CommitAsync(Set(PipelineStep.Structure, null)));
    }

    [Theory]
    [InlineData(CheckpointState.Confirmed)]
    [InlineData(CheckpointState.Skipped)]
    public async Task Set_theSameStateOnAStaleRow_reconfirmsIt(CheckpointState state)
    {
        await SeedBookAsync();
        await SeedCheckpointAsync(PipelineStep.Text, state, stale: true, at: Start.AddDays(-1));

        var effects = await AppliedAsync(Set(PipelineStep.Text, state));

        Assert.Equal(BookFacets.Checkpoints, effects.Facets);
        var row = Assert.Single(await CheckpointRowsAsync());
        Assert.Equal((state, false, Start), (row.State, row.Stale, row.At));
    }

    [Fact]
    public async Task Set_outsideTheMatrix_isRefusedWithItsSentence()
    {
        await SeedBookAsync();

        var refused = await RefusedAsync(Set(PipelineStep.Speakers, CheckpointState.Skipped));

        Assert.Equal((BookMutationRejection.Validation, "Speakers can only be confirmed."),
            (refused.Reason, refused.Message));
        Assert.Empty(await CheckpointRowsAsync());
    }

    [Fact]
    public async Task Set_onABookWithNoContent_isRefused()
    {
        await new BookHierarchyBuilder(OpenDbAsync).BuildAsync();

        var refused = await RefusedAsync(Set(PipelineStep.Structure, CheckpointState.Confirmed));

        Assert.Equal((BookMutationRejection.Validation, "Read the book first."),
            (refused.Reason, refused.Message));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private SetCheckpointMutation Set(PipelineStep step, CheckpointState? state) => new(_folder, step, state);

    private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    private async Task<BookMutationEffects> AppliedAsync(BookMutation mutation) =>
        Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(mutation)).Receipt.Effects;

    private async Task<BookMutationOutcome.Rejected> RefusedAsync(BookMutation mutation) =>
        Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(mutation));

    private Task SeedBookAsync() =>
        new BookHierarchyBuilder(OpenDbAsync)
            .AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1"))))
            .BuildAsync();
}
