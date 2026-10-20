using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.App.Queueing;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using Read2Me.Services.Queueing;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App.Queueing;

/// <summary>
/// The Character Queue's side of Background runs, over a real queue and a real project: a folder's
/// busy period on the queue is one History step, and a CancelAll closes it with how far it got.
/// Attribution's commits are made here from a DI scope that joined the runs, as the queue worker's
/// does. The voice batch's side is in <c>VoiceBatchRunnerIntegrationTests</c>.
/// </summary>
public class BackgroundRunTrackerTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private readonly CharacterQueueService _queue = new();
    private readonly BackgroundRunTracker _tracker;

    public BackgroundRunTrackerTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);

        _tracker = new BackgroundRunTracker(
            _queue,
            new VoiceBatchRunner(NullLogger<VoiceBatchRunner>.Instance, new EventBroadcaster<VoiceBatchEvent>(), new EventBroadcaster<LlmStreamEvent>()),
            new EventBroadcaster<VoiceBatchEvent>(),
            _root.GetRequiredService<BackgroundRuns>(),
            History,
            _root.GetRequiredService<IFileSystem>(),
            NullLogger<BackgroundRunTracker>.Instance);
        _tracker.Follow();
    }

    private ProjectHistory History => _root.GetRequiredService<ProjectHistory>();

    [Fact]
    public async Task AQueueThatDrains_IsOneAttributionStep_WithNoNote()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        var paragraphs = Paragraphs(_folder, 3);

        _queue.Enqueue(paragraphs);
        await AttributeAsync(new CreateCharacterMutation(_folder, "Alice"));
        _queue.Apply(paragraphs[0], new Disposition.Complete(1));
        await AttributeAsync(new CreateCharacterMutation(_folder, "Bob"));
        _queue.Apply(paragraphs[1], new Disposition.Complete(1));
        Assert.True((await History.ListAsync(_folder)).Steps[0].IsOpen);
        _queue.Apply(paragraphs[2], new Disposition.Failed("no answer"));
        await _tracker.ClosedAsync();

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution", "History started"], steps.Select(s => s.Label));
        Assert.False(steps[0].IsOpen);
        Assert.Null(steps[0].Note);
    }

    [Fact]
    public async Task EnqueuingMoreWhileBusy_ExtendsTheSameRun()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        var first = Paragraphs(_folder, 1);
        var more = Paragraphs(_folder, 1);

        _queue.Enqueue(first);
        await AttributeAsync(new CreateCharacterMutation(_folder, "Alice"));
        _queue.Enqueue(more);
        _queue.Apply(first[0], new Disposition.Complete(1));
        await AttributeAsync(new CreateCharacterMutation(_folder, "Bob"));
        _queue.Apply(more[0], new Disposition.Complete(1));
        await _tracker.ClosedAsync();

        Assert.Equal(["Attribution", "History started"], (await History.ListAsync(_folder)).Steps.Select(s => s.Label));
    }

    [Fact]
    public async Task ACancelledQueue_ClosesTheRun_WithHowManyParagraphsItGotThrough()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        var paragraphs = Paragraphs(_folder, 3);

        _queue.Enqueue(paragraphs);
        await AttributeAsync(new CreateCharacterMutation(_folder, "Alice"));
        _queue.Apply(paragraphs[0], new Disposition.Complete(1));
        _queue.CancelAll();
        await _tracker.ClosedAsync();

        var run = (await History.ListAsync(_folder)).Steps[0];
        Assert.Equal("Attribution", run.Label);
        Assert.False(run.IsOpen);
        Assert.Equal("1 of 3 paragraphs (cancelled)", run.Note);
    }

    [Fact]
    public async Task WorkLandingWhileACancelClears_StaysInTheOneCancelledRun()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        var paragraphs = Paragraphs(_folder, 2);
        _queue.Enqueue(paragraphs);
        await AttributeAsync(new CreateCharacterMutation(_folder, "Alice"));

        // In flight as the cancel begins: the worker commits and moves its item on before the clear.
        void InFlight()
        {
            AttributeAsync(new CreateCharacterMutation(_folder, "Bob")).GetAwaiter().GetResult();
            _queue.MarkProcessing(paragraphs[1]);
        }
        _queue.Cancelling += InFlight;
        try { _queue.CancelAll(); }
        finally { _queue.Cancelling -= InFlight; }
        await _tracker.ClosedAsync();

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution", "History started"], steps.Select(s => s.Label));
        Assert.Equal("0 of 2 paragraphs (cancelled)", steps[0].Note);
        Assert.False(steps[0].IsOpen);
    }

    [Fact]
    public async Task AnotherProjectsQueueWork_NeitherOpensNorKeepsOpenThisProjectsRun()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        var ours = Paragraphs(_folder, 1);
        var theirs = Paragraphs(new ProjectFolderId("another-book"), 2);

        _queue.Enqueue(theirs);
        await AttributeAsync(new CreateCharacterMutation(_folder, "Mallory")); // no run open here
        _queue.Enqueue(ours);
        await AttributeAsync(new CreateCharacterMutation(_folder, "Alice"));
        _queue.Apply(ours[0], new Disposition.Complete(1));
        await _tracker.ClosedAsync();

        var steps = (await History.ListAsync(_folder)).Steps;
        Assert.Equal(["Attribution", "Create character", "History started"], steps.Select(s => s.Label));
        Assert.All(steps, s => Assert.False(s.IsOpen));
    }

    [Fact]
    public async Task OnStart_ItClosesTheStepsAPreviousRunLeftOpen_AsInterrupted()
    {
        await SeedOneItemAsync();
        await History.ListAsync(_folder);
        _queue.Enqueue(Paragraphs(_folder, 2));
        await AttributeAsync(new CreateCharacterMutation(_folder, "Alice"));
        await _tracker.StopAsync(CancellationToken.None);

        // The app starts again: a new tracker over the same workspace, with nothing running.
        await using var restarted = BuildRestartedRoot();
        var history = restarted.GetRequiredService<ProjectHistory>();
        var tracker = new BackgroundRunTracker(
            new CharacterQueueService(),
            new VoiceBatchRunner(NullLogger<VoiceBatchRunner>.Instance, new EventBroadcaster<VoiceBatchEvent>(), new EventBroadcaster<LlmStreamEvent>()),
            new EventBroadcaster<VoiceBatchEvent>(),
            restarted.GetRequiredService<BackgroundRuns>(),
            history,
            restarted.GetRequiredService<IFileSystem>(),
            NullLogger<BackgroundRunTracker>.Instance);
        await tracker.StartAsync(CancellationToken.None);

        var run = (await history.ListAsync(_folder)).Steps[0];
        Assert.Equal("Attribution", run.Label);
        Assert.False(run.IsOpen);
        Assert.Equal("interrupted", run.Note);
        await tracker.StopAsync(CancellationToken.None);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ServiceProvider BuildRestartedRoot()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        return services.BuildServiceProvider();
    }

    /// <summary>Attribution's work: a commit from a DI scope that joined the runs, as the queue worker's does.</summary>
    private async Task AttributeAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HistoryStepScope>().JoinBackgroundRuns();
        await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    private static QueuedParagraph[] Paragraphs(ProjectFolderId folder, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new QueuedParagraph(folder, Guid.NewGuid(), $"Paragraph {i}", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
            .ToArray();

    private async Task SeedOneItemAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "Hello world"))))
            .BuildAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await _tracker.StopAsync(CancellationToken.None);
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
