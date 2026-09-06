using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.Mutations;

/// <summary>
/// The destructive structural family — the five merges, the five deletions, and clearing the Book —
/// proved through <see cref="BookMutations.CommitAsync"/> against a real SQLite project.
/// <para>
/// What matters here is what each receipt says, because a reader cannot recover from removal by
/// recounting: the merge relationship that tells a Book View which node took the deleted one's
/// place, the honest scope of a removal that cannot name what cascaded, and the difference between
/// a node the Book does not contain and a legal gesture with nothing to do — which the legacy
/// handlers answered identically.
/// </para>
/// </summary>
public class DestructiveStructuralMutationTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;

    public DestructiveStructuralMutationTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
    }

    // ── merges ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MergeVolume_ReportsTheDeletedVolumeAndTheSurvivorThatTookItsParts()
    {
        var b = await SeedTwoVolumesAsync();

        var receipt = await CommitAsync(new MergeVolumeMutation(_folder, b.VolumeId("v2"), MergeDirection.Previous));

        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        // A merge reassigns children it never names, so "exact" would be a lie however precise the
        // relationship between the two nodes is.
        Assert.Equal(BookMutationScope.WholeProject, receipt.Effects.Scope);

        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(BookStructuralRelationKind.Merge, relation.Kind);
        Assert.Equal(b.VolumeId("v2"), relation.SourceId);
        Assert.Equal(b.VolumeId("v1"), relation.ResultId);

        await using var verify = await OpenDbAsync();
        Assert.Null(await verify.Volumes.FindAsync(b.VolumeId("v2")));
        Assert.Equal(b.VolumeId("v1"), (await verify.Parts.FindAsync(b.PartId("p2")))!.VolumeId);
    }

    [Fact]
    public async Task MergeChapter_Next_ReportsTheChapterItFoldedForwardInto()
    {
        var b = await SeedTwoChaptersAsync();

        var receipt = await CommitAsync(new MergeChapterMutation(_folder, b.ChapterId("c1"), MergeDirection.Next));

        // "Merge with next" keeps the earlier node, so the survivor is the one the gesture was on.
        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(b.ChapterId("c2"), relation.SourceId);
        Assert.Equal(b.ChapterId("c1"), relation.ResultId);

        await using var verify = await OpenDbAsync();
        Assert.Null(await verify.Chapters.FindAsync(b.ChapterId("c2")));
        Assert.Equal(b.ChapterId("c1"), (await verify.Paragraphs.FindAsync(b.ParagraphId("pg2")))!.ChapterId);
    }

    [Fact]
    public async Task MergePart_ReportsTheDeletedPartAndItsSurvivor()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v
                .AddPart("p1", p => p.AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One"))))
                .AddPart("p2", p => p.AddChapter("c2", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two")))))
            .BuildAsync();

        var receipt = await CommitAsync(new MergePartMutation(_folder, b.PartId("p2"), MergeDirection.Previous));

        var relation = Assert.Single(receipt.Effects.Structural);
        Assert.Equal(b.PartId("p2"), relation.SourceId);
        Assert.Equal(b.PartId("p1"), relation.ResultId);
    }

    [Fact]
    public async Task MergeParagraph_ReportsExactEffects_BothParagraphsAndTheItemsThatMoved()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c
                .AddParagraph("pg1", g => g.AddNarration("i1", "Stays"))
                .AddParagraph("pg2", g => g.AddNarration("i2", "Moves").AddNarration("i3", "Moves too"))))
            .BuildAsync();

        var receipt = await CommitAsync(
            new MergeParagraphMutation(_folder, b.ParagraphId("pg2"), MergeDirection.Previous));

        // Nothing is removed but the emptied Paragraph, so this merge can name everything it
        // touched — and the survivor now holds items whose speakers, audio and reviews it did not
        // have, which is why the facets are the removal set rather than structure alone.
        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Audio));
        Assert.Equal([b.ParagraphId("pg1"), b.ParagraphId("pg2")], receipt.Effects.ParagraphIds);
        Assert.Equal([b.ItemId("i2"), b.ItemId("i3")], receipt.Effects.ParagraphItemIds);

        await using var verify = await OpenDbAsync();
        Assert.Equal(b.ParagraphId("pg1"), (await verify.ParagraphItems.FindAsync(b.ItemId("i2")))!.ParagraphId);
    }

    [Fact]
    public async Task MergeParagraphItem_ReportsTheItemsFacetsAsWellAsStructure()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("pg", g => g
                .AddNarration("i1", "Hello")
                .AddNarration("i2", "world"))))
            .BuildAsync();

        var receipt = await CommitAsync(
            new MergeParagraphItemMutation(_folder, b.ItemId("i1"), MergeDirection.Next));

        // The survivor's text grew and the other item is gone with its speaker, audio and review, so
        // the facets are the item-scoped ones and not structure alone.
        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.ItemText));
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Audio));
        Assert.Equal([b.ParagraphId("pg")], receipt.Effects.ParagraphIds);
        Assert.Equal([b.ItemId("i1"), b.ItemId("i2")], receipt.Effects.ParagraphItemIds);

        await using var verify = await OpenDbAsync();
        Assert.Equal("Hello world", (await verify.ParagraphItems.FindAsync(b.ItemId("i1")))!.Text);
        Assert.Null(await verify.ParagraphItems.FindAsync(b.ItemId("i2")));
    }

    [Fact]
    public async Task MergeWithNothingToMergeInto_ChangesNothing()
    {
        var b = await SeedTwoVolumesAsync();
        await using var circuit = NewCircuit();

        // The first volume has no previous sibling. A legal gesture that does nothing — not a
        // failure, and emphatically not a commit: the legacy handler answered both the same way.
        var outcome = await circuit.Mutations.CommitAsync(
            new MergeVolumeMutation(_folder, b.VolumeId("v1"), MergeDirection.Previous));

        Assert.IsType<BookMutationOutcome.NoChange>(outcome);
    }

    [Theory]
    [InlineData("volume")]
    [InlineData("part")]
    [InlineData("chapter")]
    [InlineData("paragraph")]
    [InlineData("item")]
    public async Task MergingSomethingTheBookDoesNotContain_IsRejectedAsNotFound(string level)
    {
        await SeedTwoVolumesAsync();
        var missing = Guid.NewGuid();
        BookMutation mutation = level switch
        {
            "volume" => new MergeVolumeMutation(_folder, missing, MergeDirection.Previous),
            "part" => new MergePartMutation(_folder, missing, MergeDirection.Previous),
            "chapter" => new MergeChapterMutation(_folder, missing, MergeDirection.Previous),
            "paragraph" => new MergeParagraphMutation(_folder, missing, MergeDirection.Previous),
            _ => new MergeParagraphItemMutation(_folder, missing, MergeDirection.Previous),
        };
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(mutation);

        Assert.Equal(BookMutationRejection.NotFound,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
    }

    // ── deletions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteChapter_TakesItsSubtreeAndReportsWholeProjectScope()
    {
        var b = await SeedTwoChaptersAsync();

        var receipt = await CommitAsync(new DeleteChapterMutation(_folder, b.ChapterId("c2")));

        // The cascade is the database's, so nothing below the chapter is staged here and none of it
        // can be named honestly.
        Assert.Equal(BookMutationScope.WholeProject, receipt.Effects.Scope);
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Structure));
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Attribution));
        Assert.Empty(receipt.Effects.Structural);

        await using var verify = await OpenDbAsync();
        Assert.Null(await verify.Chapters.FindAsync(b.ChapterId("c2")));
        Assert.Null(await verify.Paragraphs.FindAsync(b.ParagraphId("pg2")));
        Assert.Null(await verify.ParagraphItems.FindAsync(b.ItemId("i2")));
    }

    [Fact]
    public async Task DeleteParagraph_NamesTheParagraphAndTheItemsThatWentWithIt()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c
                .AddParagraph("pg1", g => g.AddNarration("i1", "Stays"))
                .AddParagraph("pg2", g => g.AddNarration("i2", "Goes").AddNarration("i3", "Goes too"))))
            .BuildAsync();

        var receipt = await CommitAsync(new DeleteParagraphMutation(_folder, b.ParagraphId("pg2")));

        // Small enough a subtree to read before deleting it, so this one can be exact.
        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.Equal([b.ParagraphId("pg2")], receipt.Effects.ParagraphIds);
        Assert.Equal(
            new HashSet<Guid> { b.ItemId("i2"), b.ItemId("i3") },
            receipt.Effects.ParagraphItemIds.ToHashSet());

        await using var verify = await OpenDbAsync();
        Assert.Null(await verify.Paragraphs.FindAsync(b.ParagraphId("pg2")));
        Assert.NotNull(await verify.Paragraphs.FindAsync(b.ParagraphId("pg1")));
    }

    [Fact]
    public async Task DeleteParagraphItem_NamesTheItemAndTheParagraphItLeft()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("pg", g => g
                .AddNarration("i1", "Stays")
                .AddNarration("i2", "Goes"))))
            .BuildAsync();

        var receipt = await CommitAsync(new DeleteParagraphItemMutation(_folder, b.ItemId("i2")));

        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.Equal([b.ParagraphId("pg")], receipt.Effects.ParagraphIds);
        Assert.Equal([b.ItemId("i2")], receipt.Effects.ParagraphItemIds);

        await using var verify = await OpenDbAsync();
        Assert.Null(await verify.ParagraphItems.FindAsync(b.ItemId("i2")));
        Assert.NotNull(await verify.ParagraphItems.FindAsync(b.ItemId("i1")));
    }

    [Theory]
    [InlineData("volume")]
    [InlineData("part")]
    [InlineData("chapter")]
    [InlineData("paragraph")]
    [InlineData("item")]
    public async Task DeletingSomethingTheBookDoesNotContain_IsRejectedAsNotFound(string level)
    {
        await SeedTwoVolumesAsync();
        var missing = Guid.NewGuid();
        BookMutation mutation = level switch
        {
            "volume" => new DeleteVolumeMutation(_folder, missing),
            "part" => new DeletePartMutation(_folder, missing),
            "chapter" => new DeleteChapterMutation(_folder, missing),
            "paragraph" => new DeleteParagraphMutation(_folder, missing),
            _ => new DeleteParagraphItemMutation(_folder, missing),
        };
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(mutation);

        Assert.Equal(BookMutationRejection.NotFound,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
    }

    // ── clearing the Book ────────────────────────────────────────────────────

    [Fact]
    public async Task ClearBookContent_RemovesEveryNodeAndDegradesToWholeProject()
    {
        await SeedTwoVolumesAsync();

        var receipt = await CommitAsync(new ClearBookContentMutation(_folder));

        Assert.Equal(BookMutationScope.WholeProject, receipt.Effects.Scope);
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Structure));
        Assert.Empty(receipt.Effects.Structural);
        Assert.Empty(receipt.Effects.ParagraphIds);

        await using var verify = await OpenDbAsync();
        Assert.Empty(await verify.Volumes.ToListAsync());
        Assert.Empty(await verify.Parts.ToListAsync());
        Assert.Empty(await verify.Chapters.ToListAsync());
        Assert.Empty(await verify.Paragraphs.ToListAsync());
        Assert.Empty(await verify.ParagraphItems.ToListAsync());
    }

    [Fact]
    public async Task ClearBookContent_OnAnAlreadyEmptyBook_ChangesNothing()
    {
        await SeedTwoVolumesAsync();
        await CommitAsync(new ClearBookContentMutation(_folder));
        await using var circuit = NewCircuit();

        // The reread that runs this before rebuilding should not consume a revision or make every
        // open Book View rebuild for a Book that has not moved.
        Assert.IsType<BookMutationOutcome.NoChange>(
            await circuit.Mutations.CommitAsync(new ClearBookContentMutation(_folder)));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>Two volumes, one part each, so both volumes and parts have a sibling to merge with.</summary>
    private async Task<BookHierarchyBuilder> SeedTwoVolumesAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b
            .AddVolume("v1", v => v
                .AddPart("p1", p => p.AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One")))))
            .AddVolume("v2", v => v
                .AddPart("p2", p => p.AddChapter("c2", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two")))))
            .BuildAsync();
        return b;
    }

    /// <summary>Two sibling Chapters under one implicit Part.</summary>
    private async Task<BookHierarchyBuilder> SeedTwoChaptersAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v
                .AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One")))
                .AddChapter("c2", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two"))))
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
