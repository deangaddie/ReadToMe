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
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// History over a real migrated project: real mutations commit through
/// <see cref="BookMutations.CommitAsync"/>, and what History lists is asserted through the module
/// (ADR 0016): starting History, what a Book mutation records or leaves out, and the "what changed"
/// that real mutations' effects describe. How a step scope extends, splits and closes its step is
/// <see cref="HistoryRecordingTests"/>.
/// </summary>
public class ProjectHistoryTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private readonly ProjectHistory _history;

    public ProjectHistoryTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        services.AddScoped<IBookMutationImplementation<RejectedAfterWritingMutation>, RejectedAfterWritingImplementation>();
        services.AddScoped<IBookMutationImplementation<ThrowsAfterWritingMutation>, ThrowsAfterWritingImplementation>();
        _root = services.BuildServiceProvider();

        _folder = new ProjectFolderId(FolderName);
        _history = _root.GetRequiredService<ProjectHistory>();
    }

    // ── starting History ────────────────────────────────────────────────────

    [Fact]
    public async Task AnExistingProject_StartsWithHistoryStarted_TheFirstTimeItOpens()
    {
        await SeedOneItemAsync();

        var list = await _history.ListAsync(_folder);

        var started = Assert.Single(list.Steps);
        Assert.Equal("History started", started.Label);
        Assert.Equal(HistoryStepKind.Started, started.Kind);
        Assert.Null(started.Action);
        Assert.Null(started.ParentStepId);
        Assert.Equal(1, started.Sequence);
        Assert.False(started.IsOpen);
        Assert.Equal(started.StepId, list.HeadStepId);
    }

    [Fact]
    public async Task OpeningAgain_DoesNotStartHistoryTwice()
    {
        await SeedOneItemAsync();

        await _history.ListAsync(_folder);
        var list = await _history.ListAsync(_folder);

        Assert.Single(list.Steps);
    }

    [Fact]
    public async Task ANewProject_StartsAtItsImportStep()
    {
        await SeedProjectRowAsync();

        await CommitAsync(new ImportBookContentMutation(_folder, OneChapterBook(), ReplaceExisting: false));

        var list = await _history.ListAsync(_folder);
        var import = Assert.Single(list.Steps);
        Assert.Equal("Import book content", import.Label);
        Assert.Null(import.ParentStepId);
        Assert.Equal(import.StepId, list.HeadStepId);
    }

    [Fact]
    public async Task ANewProjectOpenedBeforeItsImport_StillStartsAtTheImport()
    {
        await SeedProjectRowAsync();

        Assert.Empty((await _history.ListAsync(_folder)).Steps);
        await CommitAsync(new ImportBookContentMutation(_folder, OneChapterBook(), ReplaceExisting: false));

        Assert.Equal(["Import book content"], (await _history.ListAsync(_folder)).Steps.Select(s => s.Label));
    }

    // ── what a Book mutation records (the step scope itself: HistoryRecordingTests) ──

    [Fact]
    public async Task ACommittedStateMutation_LandsAClosedStepAtTheTip()
    {
        var b = await SeedOneItemAsync();
        var before = await _history.ListAsync(_folder);

        await CommitAsync(new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "One."));

        var list = await _history.ListAsync(_folder);
        Assert.Equal(["Insert paragraph item", "History started"], list.Steps.Select(s => s.Label));
        Assert.Equal(before.HeadStepId, list.Steps[0].ParentStepId);
        Assert.Equal(list.Steps[0].StepId, list.HeadStepId);
        Assert.False(list.Steps[0].IsOpen);
    }

    [Fact]
    public async Task ResultsOnlyCommits_RecordNothing()
    {
        var b = await SeedOneItemAsync();
        var before = await _history.ListAsync(_folder);

        var outcome = await CommitAsync(new SetParagraphItemAudioMutation(_folder, b.ItemId("item"), "audio/take.wav"));

        Assert.IsType<BookMutationOutcome.Committed>(outcome);
        Assert.Equal(before.Steps, (await _history.ListAsync(_folder)).Steps);
    }

    [Fact]
    public async Task ARejectedMutation_LeavesNoStep()
    {
        var b = await SeedOneItemAsync();
        var before = await _history.ListAsync(_folder);

        var outcome = await CommitAsync(new RejectedAfterWritingMutation(_folder, b.ParagraphId("para")));

        Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(before.Steps, (await _history.ListAsync(_folder)).Steps);
    }

    [Fact]
    public async Task AMutationThatThrows_LeavesNoStep_AndTheNextOneStillRecords()
    {
        var b = await SeedOneItemAsync();
        var before = await _history.ListAsync(_folder);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CommitAsync(new ThrowsAfterWritingMutation(_folder, b.ParagraphId("para"))));
        Assert.Equal(before.Steps, (await _history.ListAsync(_folder)).Steps);

        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var after = await _history.ListAsync(_folder);
        Assert.Equal("Create character", after.Steps[0].Label);
        Assert.Equal(before.HeadStepId, after.Steps[0].ParentStepId);
    }

    // ── what changed ────────────────────────────────────────────────────────

    [Fact]
    public async Task AStepSays_HowManyParagraphsChanged_AndTheChapterTheyAreIn()
    {
        var b = await SeedTwoChaptersAsync();

        await InScopeAsync("Edit with AI", mutations => mutations.CommitAsync(new ApplyBookEditsMutation(_folder,
        [
            new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("a1"), "First, rewritten."),
            new BookEditItem(BookEditTargetKind.ParagraphItemText, b.ItemId("a2"), "Second, rewritten."),
        ])));

        var step = (await _history.ListAsync(_folder)).Steps[0];
        Assert.Equal("Edit with AI", step.Label);
        Assert.Equal("2 paragraphs in Chapter 3", step.Summary);
    }

    [Fact]
    public async Task AnUntitledChapter_IsPlacedByItsPlaceholderTitle()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v
                .AddChapter("Named", c => c.AddParagraph("p1", p => p.AddNarration("i1", "One.")))
                .AddChapter(null, c => c.AddParagraph("p2", p => p.AddNarration("i2", "Two."))))
            .BuildAsync();

        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, b.ItemId("i2"), "Changed."));

        Assert.Equal("1 paragraph in Untitled chapter 2", (await _history.ListAsync(_folder)).Steps[0].Summary);
    }

    [Fact]
    public async Task ATitleEdit_SaysWhichNode()
    {
        var b = await SeedTwoChaptersAsync();

        await CommitAsync(new UpdateChapterTitleMutation(_folder, b.ChapterId("Chapter 3"), "The Storm"));

        Assert.Equal("The Storm", (await _history.ListAsync(_folder)).Steps[0].Summary);
    }

    [Fact]
    public async Task AStepWithNothingToPlace_HasNoSummary()
    {
        await SeedOneItemAsync();

        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));

        Assert.Null((await _history.ListAsync(_folder)).Steps[0].Summary);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

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
        await using (steps.Open(_folder, label, StepAction.Character))
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

    /// <summary>What project creation writes before there is a Book: the project row and nothing else.</summary>
    private async Task SeedProjectRowAsync()
    {
        await using var db = await OpenDbAsync();
        db.Projects.Add(new ProjectEntity { Title = "Test", BookTitle = "Test", Filename = "test.txt", Type = BookFileType.Text });
        await db.SaveChangesAsync();
    }

    private static BookContent OneChapterBook() => new([
        new VolumeContent("Vol 1", [
            new PartContent("Part 1", [
                new ChapterContent("Ch 1", [new ParagraphContent("He walked on.")]),
            ]),
        ]),
    ]);

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>Stages a real state write, then reports an expected failure.</summary>
    private sealed record RejectedAfterWritingMutation(ProjectFolderId FolderId, Guid ParagraphId) : ProjectStateMutation(FolderId);

    private sealed class RejectedAfterWritingImplementation : IBookMutationImplementation<RejectedAfterWritingMutation>
    {
        public async Task<BookMutationEffects> ApplyAsync(RejectedAfterWritingMutation mutation, ProjectDbContext db, CancellationToken ct)
        {
            await StageItemAsync(db, mutation.ParagraphId, ct);
            throw new BookMutationRejectedException(BookMutationRejection.Validation, "Refused after writing.");
        }
    }

    /// <summary>Stages a real state write, then fails with a defect.</summary>
    private sealed record ThrowsAfterWritingMutation(ProjectFolderId FolderId, Guid ParagraphId) : ProjectStateMutation(FolderId);

    private sealed class ThrowsAfterWritingImplementation : IBookMutationImplementation<ThrowsAfterWritingMutation>
    {
        public async Task<BookMutationEffects> ApplyAsync(ThrowsAfterWritingMutation mutation, ProjectDbContext db, CancellationToken ct)
        {
            await StageItemAsync(db, mutation.ParagraphId, ct);
            throw new InvalidOperationException("A defect after writing.");
        }
    }

    private static async Task StageItemAsync(ProjectDbContext db, Guid paragraphId, CancellationToken ct)
    {
        db.ParagraphItems.Add(new ParagraphItem
        {
            Id = Guid.NewGuid(), ParagraphId = paragraphId, Text = "Staged.", Order = "z", ItemType = ParagraphItemType.Speech,
        });
        await db.SaveChangesAsync(ct);
    }
}
