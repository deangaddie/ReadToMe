using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// History stays fast to open and survives schema upgrades (ADR 0016, "Snapshots" and "Viewing"):
/// a snapshot every N closed steps and after a schema migration on open, steps opened from the
/// nearest earlier snapshot and migrated, a step whose old schema fails to migrate marked
/// can't-be-opened, and a cache of a few materialised steps.
/// </summary>
public class SnapshotTests : ProjectDbTestBase
{
    private readonly ProjectFolderId _folder;
    private readonly ManualTimeProvider _clock = new();
    private ServiceProvider _root;

    public SnapshotTests()
    {
        _folder = new ProjectFolderId(FolderName);
        _root = NewApp(snapshotEvery: 3);
    }

    private ProjectHistory History => _root.GetRequiredService<ProjectHistory>();

    private string HistoryFolder => Path.Combine(FolderPath, "history");

    private string[] Snapshots() => Directory.GetFiles(Path.Combine(HistoryFolder, "snapshots"), "*.db");

    [Fact]
    public async Task EveryNClosedSteps_AddsASnapshot_AndEveryStepStillReadsBackItsState()
    {
        await SeedAsync();
        var steps = new List<(Guid Step, string[] Cast)> { (await TipAsync(), []) };
        var cast = new List<string>();
        foreach (var name in new[] { "A", "B", "C", "D", "E", "F", "G", "H" })
        {
            await CommitAsync(new CreateCharacterMutation(_folder, name));
            cast.Add(name);
            steps.Add((await TipAsync(), [.. cast]));
        }

        // Nine steps, a snapshot after every third, beside the base one.
        Assert.Equal(1 + 9 / 3, Snapshots().Length);
        foreach (var (step, expected) in steps)
            Assert.Equal(expected, await CastAtAsync(step));
    }

    [Fact]
    public async Task AStepPastASnapshot_OpensFromIt()
    {
        await SeedAsync();
        foreach (var name in new[] { "A", "B", "C" })
            await CommitAsync(new CreateCharacterMutation(_folder, name));
        var tip = await TipAsync();

        // Every step from the snapshot on is the snapshot plus its own chunks.
        File.Delete(Path.Combine(HistoryFolder, "snapshots", "base.db"));

        Assert.Equal(["A", "B", "C"], await CastAtAsync(tip));
    }

    [Fact]
    public async Task AStepScopeClosing_CountsTowardTheNextSnapshot()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "A"));
        await using (var scope = _root.CreateAsyncScope())
        await using (scope.ServiceProvider.GetRequiredService<HistoryStepScope>().Open(_folder, "Cast", StepAction.Character))
        {
            await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(new CreateCharacterMutation(_folder, "B"));
            Assert.Single(Snapshots()); // the step is still open
        }

        Assert.Equal(2, Snapshots().Length);
        Assert.Equal(["A", "B"], await CastAtAsync(await TipAsync()));
    }

    [Fact]
    public async Task AStepRecordedBeforeAMigration_OpensMigrated_AndStepsAfterItReadCorrectly()
    {
        await SeedAsync();
        var started = await TipAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var alice = await TipAsync();
        await CastAtAsync(alice); // cached by the older build

        await RestartOnPriorSchemaAsync();
        await History.StartAsync(_folder); // opening the project migrates it
        Assert.Equal(2, Snapshots().Length);

        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var bob = await TipAsync();

        Assert.Empty(await CastAtAsync(started));
        Assert.Equal(["Alice"], await CastAtAsync(alice));
        Assert.Equal(["Alice", "Bob"], await CastAtAsync(bob));
    }

    [Fact]
    public async Task AMigrationOnOpen_IsSnapshottedBeforeTheNextCommit_EvenWhenNothingListsHistory()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var alice = await TipAsync();

        await RestartOnPriorSchemaAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));

        Assert.Equal(2, Snapshots().Length);
        Assert.Equal(["Alice"], await CastAtAsync(alice));
        Assert.Equal(["Alice", "Bob"], await CastAtAsync(await TipAsync()));
    }

    [Fact]
    public async Task AStepWhoseOldSchemaFailsToMigrate_IsMarkedCantBeOpened_AndAnnounced()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var alice = await TipAsync();
        await BreakBaseSnapshotAsync();
        var changed = new List<HistoryStepEvent>();
        _root.GetRequiredService<EventBroadcaster<HistoryStepEvent>>().Event += changed.Add;

        var opening = await History.OpenStepAsync(_folder, alice);

        var cantOpen = Assert.IsType<StepOpening.CantOpen>(opening);
        Assert.True(cantOpen.Step.CantOpen);
        Assert.True((await History.ListAsync(_folder)).Steps.Single(s => s.StepId == alice).CantOpen);
        var announced = Assert.Single(changed);
        Assert.Equal((HistoryStepEventKind.Changed, alice, true), (announced.Kind, announced.Step.StepId, announced.Step.CantOpen));
        Assert.IsType<StepOpening.CantOpen>(await History.OpenStepAsync(_folder, alice));
        Assert.Empty(Directory.GetFiles(Path.Combine(HistoryFolder, "cache")));
    }

    [Fact]
    public async Task TheCache_KeepsAFewSteps_AndDropsTheLeastRecentlyOpened()
    {
        await SeedAsync();
        var steps = new List<Guid> { await TipAsync() };
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            await CommitAsync(new CreateCharacterMutation(_folder, name));
            steps.Add(await TipAsync());
        }

        // Opened a few minutes apart, so none is still being read.
        foreach (var step in new[] { steps[0], steps[1], steps[2], steps[3], steps[0], steps[4] })
        {
            await CastAtAsync(step);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        var cached = Directory.GetFiles(Path.Combine(HistoryFolder, "cache"))
            .Select(f => Guid.ParseExact(Path.GetFileName(f)[..32], "N")).ToHashSet();
        Assert.Equal(new HashSet<Guid> { steps[0], steps[2], steps[3], steps[4] }, cached);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ServiceProvider NewApp(int snapshotEvery)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.Configure<HistoryOptions>(o => o.SnapshotEvery = snapshotEvery);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// As if everything so far was recorded by the app before its latest migration, which is then
    /// started again: the project, its snapshots and its cached steps taken back to <see cref="PriorSchema.MigrationId"/>.
    /// The state tables are the same on both sides of it (PriorSchema guards that), so the chunks hold for either.
    /// </summary>
    private async Task RestartOnPriorSchemaAsync()
    {
        await _root.DisposeAsync();
        await PriorSchema.MigrateAsync(Path.Combine(FolderPath, "project.db"));
        var cache = Path.Combine(HistoryFolder, "cache");
        foreach (var copy in Snapshots().Concat(Directory.Exists(cache) ? Directory.GetFiles(cache, "*.db") : []))
            await PriorSchema.MigrateAsync(copy);
        _root = NewApp(snapshotEvery: 50);
    }

    /// <summary>The base snapshot broken so that migrating any step opened from it fails.</summary>
    private Task BreakBaseSnapshotAsync() => PriorSchema.BreakAsync(Path.Combine(HistoryFolder, "snapshots", "base.db"));

    private async Task SeedAsync()
    {
        await new BookHierarchyBuilder(OpenDbAsync).WithProject()
            .AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "Hello world"))))
            .BuildAsync();
    }

    private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    private async Task<Guid> TipAsync() => (await History.ListAsync(_folder)).HeadStepId!.Value;

    /// <summary>The named characters at <paramref name="stepId"/>, read as a request at <c>?at=</c> reads them.</summary>
    private async Task<string[]> CastAtAsync(Guid stepId)
    {
        var opened = Assert.IsType<StepOpening.Opened>(await History.OpenStepAsync(_folder, stepId));
        await using var session = new ProjectDbSession(
            _root.GetRequiredService<IFileSystem>(), _root.GetRequiredService<IProjectDbContextFactory>(),
            NullLogger<ProjectDbSession>.Instance);
        session.ReadAt(_folder, opened.State);
        var characters = await new ProjectReader(session, NullLogger<ProjectReader>.Instance).GetCharactersAsync(_folder);
        return characters.Where(c => !c.IsNarrator).Select(c => c.Name).Order().ToArray();
    }

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
