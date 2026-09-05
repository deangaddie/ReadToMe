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
/// Imports and rereads, proved through <see cref="BookMutations.CommitAsync"/> against a real
/// SQLite project.
/// <para>
/// The claim that matters here is that a reread is <em>one</em> commit. Clearing and repopulating as
/// two would publish a receipt for the empty Book in between, and every other open Book View would
/// dutifully rebuild against it — the reader would watch their project empty itself and refill. So
/// the tests below check the receipt count and the Book at commit time, not just the end state.
/// </para>
/// </summary>
public class ImportMutationTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;

    public ImportMutationTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
    }

    [Fact]
    public async Task Import_IntoAnEmptyBook_AddsTheContentAndDegradesToWholeProject()
    {
        await SeedEmptyProjectAsync();

        var receipt = await CommitAsync(
            new ImportBookContentMutation(_folder, OneChapter("Hello there."), ReplaceExisting: false));

        // Every node in the Book is new, so naming them would be an inventory rather than a hint.
        Assert.Equal(BookMutationScope.WholeProject, receipt.Effects.Scope);
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Structure));
        Assert.Empty(receipt.Effects.ParagraphIds);

        await using var verify = await OpenDbAsync();
        Assert.Single(await verify.Volumes.ToListAsync());
        Assert.Single(await verify.Paragraphs.ToListAsync());
    }

    [Fact]
    public async Task Reread_ReplacesEveryNodeInOneCommit()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v
                .AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "The old text."))))
            .BuildAsync();

        var receipt = await CommitAsync(
            new ImportBookContentMutation(_folder, OneChapter("The new text."), ReplaceExisting: true));

        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Structure));
        // A reread takes each item's text, speaker, audio and review with it.
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Attribution));
        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.Audio));

        await using var verify = await OpenDbAsync();
        Assert.Null(await verify.Volumes.FindAsync(b.VolumeId("v1")));
        Assert.Single(await verify.Volumes.ToListAsync());
        var item = Assert.Single(await verify.ParagraphItems.ToListAsync());
        Assert.Equal("The new text.", item.Text);
    }

    [Fact]
    public async Task Reread_PublishesOneReceipt_AndNeverAnEmptyBook()
    {
        await new BookHierarchyBuilder(OpenDbAsync)
            .AddVolume("v1", v => v
                .AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "The old text."))))
            .BuildAsync();

        // What a converging Book View would see: the Book as it stands the moment each receipt is
        // published. If the replacement were two commits, one of these would find nothing.
        var booksSeen = new List<int>();
        var broadcaster = _root.GetRequiredService<Read2Me.Services.Events.EventBroadcaster<BookMutationReceipt>>();
        broadcaster.Event += _ =>
        {
            using var db = OpenDbAsync().GetAwaiter().GetResult();
            booksSeen.Add(db.Volumes.Count());
        };

        await CommitAsync(new ImportBookContentMutation(_folder, OneChapter("The new text."), ReplaceExisting: true));

        Assert.Equal([1], booksSeen);
    }

    [Fact]
    public async Task Import_OfNothingIntoAnEmptyBook_ChangesNothing()
    {
        await SeedEmptyProjectAsync();

        await using var circuit = NewCircuit();
        var outcome = await circuit.Mutations.CommitAsync(
            new ImportBookContentMutation(_folder, new BookContent([]), ReplaceExisting: true));

        // No revision, no receipt, and no Book View anywhere rebuilding for a Book that has not moved.
        Assert.IsType<BookMutationOutcome.NoChange>(outcome);
    }

    [Fact]
    public async Task Import_WithoutAProjectRecord_IsRefusedRatherThanThrowing()
    {
        // The schema, and nothing in it.
        await using var _ = await OpenDbAsync();

        await using var circuit = NewCircuit();
        var outcome = await circuit.Mutations.CommitAsync(
            new ImportBookContentMutation(_folder, OneChapter("Hello there."), ReplaceExisting: false));

        var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
    }

    // ── the staged cover image ───────────────────────────────────────────────

    [Fact]
    public async Task Import_NamesTheStagedCover_WhenTheProjectHasNone()
    {
        await SeedEmptyProjectAsync();

        var receipt = await CommitAsync(new ImportBookContentMutation(
            _folder, OneChapter("Hello there."), ReplaceExisting: false, CoverImageFileName: "cover.jpg"));

        Assert.True(receipt.Effects.Facets.HasFlag(BookFacets.ProjectPolicy));
        await using var verify = await OpenDbAsync();
        Assert.Equal("cover.jpg", (await verify.Projects.SingleAsync()).CoverImage);
    }

    [Fact]
    public async Task Import_LeavesACoverTheReaderAlreadyChose()
    {
        await SeedEmptyProjectAsync(coverImage: "chosen.png");

        var receipt = await CommitAsync(new ImportBookContentMutation(
            _folder, OneChapter("Hello there."), ReplaceExisting: false, CoverImageFileName: "cover.jpg"));

        Assert.False(receipt.Effects.Facets.HasFlag(BookFacets.ProjectPolicy));
        await using var verify = await OpenDbAsync();
        Assert.Equal("chosen.png", (await verify.Projects.SingleAsync()).CoverImage);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static BookContent OneChapter(string text) =>
        new([new VolumeContent("Volume", [new PartContent(null, [new ChapterContent("Chapter", [new ParagraphContent(text)])])])]);

    private async Task SeedEmptyProjectAsync(string? coverImage = null)
    {
        await using var db = await OpenDbAsync();
        db.Projects.Add(new Read2Me.Data.Entities.Project
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            BookTitle = "Test Book",
            Author = "Author",
            Filename = "book.txt",
            Type = Read2Me.Data.Enums.BookFileType.Text,
            CoverImage = coverImage,
        });
        await db.SaveChangesAsync();
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
