using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// <b>View a step</b> over a real migrated project: real mutations commit through
/// <see cref="BookMutations.CommitAsync"/>, a step is opened through <see cref="ProjectHistory"/>,
/// and the past state is read back through the ordinary project readers, with the project DB
/// session resolving the state reference (spec, "Read side: a state reference").
/// </summary>
public class OpenStepTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private readonly ProjectHistory _history;

    public OpenStepTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();

        _folder = new ProjectFolderId(FolderName);
        _history = _root.GetRequiredService<ProjectHistory>();
    }

    [Fact]
    public async Task OpeningAnyStep_ReadsBackTheProjectAsItStoodAfterIt()
    {
        var b = await SeedOneItemAsync();
        var started = await TipAsync();

        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var alice = (await LiveReader().GetCharactersAsync(_folder)).Single(c => c.Name == "Alice").Id;
        var created = await TipAsync();

        await CommitAsync(new AddCharacterAliasMutation(_folder, alice, "Al"));
        await CommitAsync(new CreateVoiceMutation(_folder, alice, "Warm"));
        var voice = (await LiveReader().GetCharacterVoicesAsync(_folder, alice)).Single().Id;
        await CommitAsync(new CreateVoiceRuleMutation(_folder, alice, voice, VoiceAnchorLevel.Chapter, b.ChapterId("ch"), null, null));
        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, b.ItemId("item"), "Goodbye world"));
        await CommitAsync(new UpdateProjectMetadataMutation(_folder, "Renamed", null, "Someone"));
        var cast = await TipAsync();

        await CommitAsync(new DeleteCharacterMutation(_folder, alice));
        var deleted = await TipAsync();

        // History started: the book as seeded, no cast.
        await using (var at = await ReaderAtAsync(started))
        {
            Assert.Empty(await NamedCharactersAsync(at.Reader));
            Assert.Equal("Hello world", await ItemTextAsync(at.Reader, b));
            Assert.Equal("Test Book", (await at.Reader.GetProjectAsync(_folder))!.Title);
        }

        // After "Create character": Alice, nothing else yet.
        await using (var at = await ReaderAtAsync(created))
        {
            var only = Assert.Single(await NamedCharactersAsync(at.Reader));
            Assert.Equal("Alice", only.Name);
            Assert.Empty(only.Aliases);
            Assert.Empty(only.Voices);
        }

        // After the metadata edit: everything up to it.
        await using (var at = await ReaderAtAsync(cast))
        {
            var only = Assert.Single(await NamedCharactersAsync(at.Reader));
            Assert.Equal(["Al"], only.Aliases.Select(a => a.Name));
            Assert.Equal(["Warm"], only.Voices.Select(v => v.Name));
            var rule = Assert.Single(await at.Reader.GetCharacterVoiceRulesAsync(_folder, alice), r => !r.IsDefault);
            Assert.Equal(b.ChapterId("ch"), rule.FromNodeId);
            Assert.Equal("Goodbye world", await ItemTextAsync(at.Reader, b));
            var project = (await at.Reader.GetProjectAsync(_folder))!;
            Assert.Equal("Renamed", project.Title);
            Assert.Equal("Someone", project.Author);
        }

        // After the delete: Alice gone again, the edits kept.
        await using (var at = await ReaderAtAsync(deleted))
        {
            Assert.Empty(await NamedCharactersAsync(at.Reader));
            Assert.Equal("Goodbye world", await ItemTextAsync(at.Reader, b));
        }
    }

    [Fact]
    public async Task ADeleteThatCascaded_OpensOnBothSides()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.WithProject()
            .AddVolume("vol", v => v
                .AddChapter("ch1", c => c.AddParagraph("p1", p => p.AddNarration("i1", "One.")))
                .AddChapter("ch2", c => c.AddParagraph("p2", p => p.AddNarration("i2", "Two."))))
            .BuildAsync();
        var before = await TipAsync();

        await CommitAsync(new DeleteChapterMutation(_folder, b.ChapterId("ch1")));
        var after = await TipAsync();

        await using (var at = await ReaderAtAsync(before))
            Assert.Equal(["One.", "Two."], await BookTextAsync(at.Reader));
        await using (var at = await ReaderAtAsync(after))
            Assert.Equal(["Two."], await BookTextAsync(at.Reader));
    }

    [Fact]
    public async Task ANewProject_OpensAtItsImportStep()
    {
        await using (var db = await OpenDbAsync())
        {
            db.Projects.Add(new ProjectEntity
                { Title = "Test", BookTitle = "Test", Filename = "test.txt", Type = BookFileType.Text });
            await db.SaveChangesAsync();
        }
        await CommitAsync(new ImportBookContentMutation(_folder, new BookContent([
            new VolumeContent("Vol 1", [new PartContent("Part 1", [new ChapterContent("Ch 1", [new ParagraphContent("He walked on.")])])]),
        ]), ReplaceExisting: false));

        await using var at = await ReaderAtAsync(await TipAsync());

        Assert.Equal(["He walked on."], await BookTextAsync(at.Reader));
    }

    [Fact]
    public async Task AStepStillOpen_ReadsWhatItHoldsSoFar_AndWhatItHoldsOnceItGrows()
    {
        await SeedOneItemAsync();
        await _history.ListAsync(_folder);

        await using var scope = _root.CreateAsyncScope();
        var mutations = scope.ServiceProvider.GetRequiredService<BookMutations>();
        await using (scope.ServiceProvider.GetRequiredService<HistoryStepScope>().Open(_folder, "Cast"))
        {
            await mutations.CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
            var open = await TipAsync();
            await using (var at = await ReaderAtAsync(open))
                Assert.Equal(["Alice"], (await NamedCharactersAsync(at.Reader)).Select(c => c.Name));

            await mutations.CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
            Assert.Equal(open, await TipAsync());
            await using (var at = await ReaderAtAsync(open))
                Assert.Equal(["Alice", "Bob"], (await NamedCharactersAsync(at.Reader)).Select(c => c.Name));
        }
    }

    [Fact]
    public async Task AnUnknownStep_IsUnknown_EvenBeforeTheProjectHasHistory()
    {
        await SeedOneItemAsync();

        Assert.IsType<StepOpening.UnknownStep>(await _history.OpenStepAsync(_folder, Guid.NewGuid()));
        await _history.ListAsync(_folder);
        Assert.IsType<StepOpening.UnknownStep>(await _history.OpenStepAsync(_folder, Guid.NewGuid()));
    }

    [Fact]
    public async Task OpeningAStep_NeverTouchesProjectDb_AndTheLiveProjectStaysWritable()
    {
        await SeedOneItemAsync();
        var started = await TipAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var projectDb = Path.Combine(FolderPath, "project.db");
        var bytes = await File.ReadAllBytesAsync(projectDb);
        var written = File.GetLastWriteTimeUtc(projectDb);

        await using var at = await ReaderAtAsync(started);
        Assert.Empty(await NamedCharactersAsync(at.Reader)); // the past context is open and holding its file

        Assert.Equal(bytes, await File.ReadAllBytesAsync(projectDb));
        Assert.Equal(written, File.GetLastWriteTimeUtc(projectDb));
        Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(new CreateCharacterMutation(_folder, "Bob")));
        Assert.Equal(["Alice", "Bob"],
            (await LiveReader().GetCharactersAsync(_folder)).Where(c => !c.IsNarrator).Select(c => c.Name).Order());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<List<string?>> BookTextAsync(ProjectReader reader)
    {
        var text = new List<string?>();
        foreach (var volume in await reader.GetVolumesAsync(_folder))
        foreach (var part in await reader.GetPartsAsync(_folder, volume.Id))
        foreach (var chapter in await reader.GetChaptersAsync(_folder, part.Id))
        foreach (var paragraph in await reader.GetChapterParagraphsAsync(_folder, chapter.Id))
            text.AddRange(paragraph.Items.Select(i => i.Text));
        return text;
    }

    private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    private async Task<Guid> TipAsync() => (await _history.ListAsync(_folder)).HeadStepId!.Value;

    private ProjectReader LiveReader() => new(NewSession(), NullLogger<ProjectReader>.Instance);

    private ProjectDbSession NewSession() => new(
        _root.GetRequiredService<IFileSystem>(), _root.GetRequiredService<IProjectDbContextFactory>(),
        NullLogger<ProjectDbSession>.Instance);

    /// <summary>A request reading at <paramref name="stepId"/>: a session holding the step as its state reference.</summary>
    private async Task<PastReader> ReaderAtAsync(Guid stepId)
    {
        var opening = await _history.OpenStepAsync(_folder, stepId);
        var opened = Assert.IsType<StepOpening.Opened>(opening);
        var session = NewSession();
        session.ReadAt(_folder, opened.State);
        return new PastReader(session, new ProjectReader(session, NullLogger<ProjectReader>.Instance));
    }

    private sealed record PastReader(ProjectDbSession Session, ProjectReader Reader) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private async Task<List<Character>> NamedCharactersAsync(ProjectReader reader) =>
        (await reader.GetCharactersWithAliasesAsync(_folder)).Where(c => !c.IsNarrator).ToList();

    private async Task<string?> ItemTextAsync(ProjectReader reader, BookHierarchyBuilder b) =>
        (await reader.GetChapterParagraphsAsync(_folder, b.ChapterId("ch"))).Single().Items.Single().Text;

    private async Task<BookHierarchyBuilder> SeedOneItemAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.WithProject()
            .AddVolume("vol", v => v.AddChapter("ch", c => c
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
