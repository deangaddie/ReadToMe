using Microsoft.EntityFrameworkCore;
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
/// The additive structural family — the four splits, the title and pause sweeps, and pause
/// insertion — proved through <see cref="BookMutations.CommitAsync"/> against a real SQLite
/// project. What matters here is not that the nodes move (the planners are tested on their own)
/// but that each receipt describes the effects actually applied: the split relationship a Book View
/// keeps the reader's place by, the honest whole-project scope of a sweep, and the
/// <c>NoChange</c> a sweep with nothing to do finally reports instead of a silent empty commit.
/// </summary>
public class AdditiveStructuralMutationTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;

    public AdditiveStructuralMutationTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
    }

    // ── splits ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SplitAtPart_ReportsTheSourceVolumeAndTheVolumeItCreated()
    {
        var b = await SeedTwoVolumesTwoPartsAsync();

        var receipt = await CommitAsync(new SplitAtPartMutation(_folder, b.PartId("p2"), "Volume Two"));

        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        // A split moves the source's later children into the new node without naming them, so
        // "exact" would be a lie however precise the relationship is.
        Assert.Equal(BookMutationScope.WholeProject, receipt.Effects.Scope);

        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(BookStructuralRelationKind.Split, relation.Kind);
        Assert.Equal(b.VolumeId("v1"), relation.SourceId);
        Assert.Equal(receipt.Effects.CreatedId, relation.ResultId);

        await using var verify = await OpenDbAsync();
        var created = await verify.Volumes.FindAsync(relation.ResultId);
        Assert.Equal("Volume Two", created!.Title);
        Assert.Equal(created.Id, (await verify.Parts.FindAsync(b.PartId("p2")))!.VolumeId);
    }

    [Fact]
    public async Task SplitAtChapter_ReportsTheSourcePartAndThePartItCreated()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddPart("p1", p => p
                .AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One")))
                .AddChapter("c2", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two")))))
            .BuildAsync();

        var receipt = await CommitAsync(new SplitAtChapterMutation(_folder, b.ChapterId("c2"), null));

        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(b.PartId("p1"), relation.SourceId);
        Assert.Equal(receipt.Effects.CreatedId, relation.ResultId);

        await using var verify = await OpenDbAsync();
        Assert.Equal(relation.ResultId, (await verify.Chapters.FindAsync(b.ChapterId("c2")))!.PartId);
    }

    [Fact]
    public async Task SplitAtParagraph_ReportsTheSourceChapterAndTheChapterItCreated()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c
                .AddParagraph("pg1", g => g.AddNarration("i1", "One"))
                .AddParagraph("pg2", g => g.AddNarration("i2", "Two"))))
            .BuildAsync();

        var receipt = await CommitAsync(new SplitAtParagraphMutation(_folder, b.ParagraphId("pg2"), "Chapter Two"));

        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(b.ChapterId("c1"), relation.SourceId);

        await using var verify = await OpenDbAsync();
        Assert.Equal("Chapter Two", (await verify.Chapters.FindAsync(relation.ResultId))!.Title);
        Assert.Equal(relation.ResultId, (await verify.Paragraphs.FindAsync(b.ParagraphId("pg2")))!.ChapterId);
    }

    [Fact]
    public async Task SplitAtItem_ReportsExactEffects_BothParagraphsAndTheItemsThatMoved()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("pg", g => g
                .AddNarration("i1", "Stays")
                .AddNarration("i2", "Moves")
                .AddNarration("i3", "Moves too"))))
            .BuildAsync();

        var receipt = await CommitAsync(new SplitAtItemMutation(_folder, b.ItemId("i2")));

        // The one split that can name everything it touched: two Paragraphs and the items between.
        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        var created = receipt.Effects.CreatedId!.Value;
        Assert.Equal([b.ParagraphId("pg"), created], receipt.Effects.ParagraphIds);
        Assert.Equal([b.ItemId("i2"), b.ItemId("i3")], receipt.Effects.ParagraphItemIds);

        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(b.ParagraphId("pg"), relation.SourceId);
        Assert.Equal(created, relation.ResultId);
    }

    [Theory]
    [InlineData("part")]
    [InlineData("chapter")]
    [InlineData("paragraph")]
    [InlineData("item")]
    public async Task SplitAtSomethingTheBookDoesNotContain_IsRejectedAsNotFound(string level)
    {
        await SeedTwoVolumesTwoPartsAsync();
        var missing = Guid.NewGuid();
        BookMutation mutation = level switch
        {
            "part" => new SplitAtPartMutation(_folder, missing, null),
            "chapter" => new SplitAtChapterMutation(_folder, missing, null),
            "paragraph" => new SplitAtParagraphMutation(_folder, missing, null),
            _ => new SplitAtItemMutation(_folder, missing),
        };
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(mutation);

        var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);

        await using var verify = await OpenDbAsync();
        Assert.Equal(2, await verify.Volumes.CountAsync());
    }

    // ── title and pause sweeps ───────────────────────────────────────────────

    [Fact]
    public async Task AddChapterTitles_SweepsTheWholeBook_AndSaysSo()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v
                .AddChapter("Chapter One", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One")))
                .AddChapter("Chapter Two", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two"))))
            .BuildAsync();

        var receipt = await CommitAsync(new AddChapterTitlesMutation(_folder));

        Assert.Equal(BookMutationScope.WholeProject, receipt.Effects.Scope);
        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        Assert.Empty(receipt.Effects.ParagraphIds);

        await using var verify = await OpenDbAsync();
        var spoken = await verify.ParagraphItems.Select(i => i.Text).ToListAsync();
        Assert.Contains("Chapter One", spoken);
        Assert.Contains("Chapter Two", spoken);
    }

    [Fact]
    public async Task AddBookTitle_PutsTheTitleAndAuthorAtTheFrontOfTheBook()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithProject(title: "A Study in Scarlet", author: "Arthur Conan Doyle");
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("pg", g => g.AddNarration("i1", "One"))))
            .BuildAsync();

        await CommitAsync(new AddBookTitleMutation(_folder));

        await using var verify = await OpenDbAsync();
        var spoken = await verify.ParagraphItems.Select(i => i.Text).ToListAsync();
        Assert.Contains("A Study in Scarlet", spoken);
        Assert.Contains("By Arthur Conan Doyle", spoken);
    }

    [Fact]
    public async Task AddChapterTitles_OnAnUntitledBook_ChangesNothingAndConsumesNoRevision()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter(null, c => c.AddParagraph("pg", g => g.AddNarration("i1", "One"))))
            .BuildAsync();
        await using var circuit = NewCircuit();

        Assert.IsType<BookMutationOutcome.NoChange>(
            await circuit.Mutations.CommitAsync(new AddChapterTitlesMutation(_folder)));
    }

    [Fact]
    public async Task AddPauses_RunTwice_CommitsOnceAndThenReportsNoChange()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c
                .AddParagraph("pg1", g => g.AddNarration("i1", "One"))
                .AddParagraph("pg2", g => g.AddNarration("i2", "Two"))))
            .BuildAsync();
        await using var circuit = NewCircuit();

        var first = await circuit.Mutations.CommitAsync(new AddPausesMutation(_folder));
        Assert.IsType<BookMutationOutcome.Committed>(first);

        // The gesture the legacy handler could not report honestly: nothing left to insert, so no
        // revision, no receipt, and no success for a Book View to announce.
        var second = await circuit.Mutations.CommitAsync(new AddPausesMutation(_folder));
        Assert.IsType<BookMutationOutcome.NoChange>(second);
    }

    // ── pause insertion ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(InsertPosition.Before, 0)]
    [InlineData(InsertPosition.After, 1)]
    public async Task InsertPauseParagraph_PlacesOnePauseBesideTheAnchorsParagraph(
        InsertPosition position, int expectedIndex)
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c
                .AddParagraph("pg1", g => g.AddNarration("i1", "One"))
                .AddParagraph("pg2", g => g.AddNarration("i2", "Two"))))
            .BuildAsync();

        var receipt = await CommitAsync(
            new InsertPauseParagraphMutation(_folder, b.ItemId("i1"), position, PauseKind.ChapterPause));

        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        var created = receipt.Effects.CreatedId!.Value;
        Assert.Equal(created, Assert.Single(receipt.Effects.ParagraphIds));
        Assert.Empty(receipt.Effects.Structural);

        await using var verify = await OpenDbAsync();
        var ordered = await verify.Paragraphs
            .Where(p => p.ChapterId == b.ChapterId("c1"))
            .OrderBy(p => p.Order)
            .Select(p => p.Id)
            .ToListAsync();
        Assert.Equal(created, ordered[expectedIndex]);

        var item = await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == created);
        Assert.Equal(ParagraphItemType.ChapterPause, item.ItemType);
    }

    [Fact]
    public async Task InsertPauseParagraph_AgainstAnAnchorTheBookDoesNotContain_IsRejectedAsNotFound()
    {
        await SeedTwoVolumesTwoPartsAsync();
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(
            new InsertPauseParagraphMutation(_folder, Guid.NewGuid(), InsertPosition.After, PauseKind.Pause));

        Assert.Equal(BookMutationRejection.NotFound,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task<BookHierarchyBuilder> SeedTwoVolumesTwoPartsAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b
            .AddVolume("v1", v => v
                .AddPart("p1", p => p.AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One"))))
                .AddPart("p2", p => p.AddChapter("c2", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two")))))
            .AddVolume("v2", v => v
                .AddChapter("c3", c => c.AddParagraph("pg3", g => g.AddNarration("i3", "Three"))))
            .BuildAsync();
        return b;
    }

    private async Task<BookMutationReceipt> CommitAsync(BookMutation mutation)
    {
        await using var circuit = NewCircuit();
        var outcome = await circuit.Mutations.CommitAsync(mutation);
        return Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
    }

    private sealed class Circuit(AsyncServiceScope scope) : IAsyncDisposable
    {
        public BookMutations Mutations { get; } = scope.ServiceProvider.GetRequiredService<BookMutations>();
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private Circuit NewCircuit() => new(_root.CreateAsyncScope());

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
