using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.Pronunciations;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Mutations;

/// <summary>
/// A Pronunciation add, edit or delete through <see cref="BookMutations.CommitAsync"/> over a real
/// project: the list it leaves, and the audio it discards — that of every item with audio whose
/// Spoken text the change alters, and no other. The receipt names those items; the API counts them.
/// </summary>
public class PronunciationMutationTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private BookHierarchyBuilder _book = null!;
    private int _items;

    public PronunciationMutationTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
    }

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }

    // ── a change discards the audio whose Spoken text it alters ─────────────

    [Fact]
    public async Task AnAdd_DiscardsTheAudioOfTheItemsItNewlyMatches_AndStoresTheNormalisedForms()
    {
        await SeedAsync("Holmes ran.", "Holmes slept.", "Watson read.");
        await GiveAudioAsync("n1", "n3");

        var effects = await AppliedAsync(new PronunciationDraft(null, "  Holmes ", " Hol   mz "));

        Assert.Equal([_book.ItemId("n1")], effects.ParagraphItemIds); // n2 matches too, but has no audio to lose
        // The receipt's Pronunciations facet is what an open Book view rereads its marks on.
        Assert.True(effects.Facets.HasFlag(BookFacets.Pronunciations));
        Assert.Equal(["n3"], await WithAudioAsync());
        var stored = Assert.Single(await ListAsync());
        Assert.Equal((effects.CreatedId, "Holmes", "Hol mz"), (stored.Id, stored.WrittenForm, stored.SpokenForm));
    }

    [Fact]
    public async Task ASpokenFormEdit_DiscardsTheAudioOfTheEntrysMatches()
    {
        await SeedAsync("Holmes ran.", "Holmes slept.", "Watson read.");
        var holmes = await AddAsync("Holmes", "Holmz");
        await GiveAudioAsync("n1", "n3");

        var effects = await AppliedAsync(new PronunciationDraft(holmes, "Holmes", "Hohmz"));

        Assert.Equal([_book.ItemId("n1")], effects.ParagraphItemIds);
        Assert.Equal(["n3"], await WithAudioAsync());
        Assert.Equal("Hohmz", Assert.Single(await ListAsync()).SpokenForm);
    }

    [Fact]
    public async Task AWrittenFormEdit_DiscardsTheAudioOfItsOldAndNewMatches()
    {
        await SeedAsync("Holmes ran.", "Lestrade slept.", "Watson read.");
        var holmes = await AddAsync("Holmes", "Holmz");
        await GiveAudioAsync("n1", "n2", "n3");

        var effects = await AppliedAsync(new PronunciationDraft(holmes, "Watson", "Holmz"));

        Assert.Equal(2, effects.ParagraphItemIds.Count);
        Assert.Equal(["n2"], await WithAudioAsync());
    }

    [Fact]
    public async Task AnOverlappingAdd_DiscardsOnlyWhereTheWinnerChangesWhatIsSaid()
    {
        await SeedAsync("Van Helsing fell.", "Helsing stood.", "Van wept.");
        await AddAsync("Helsing", "HEL-sing");
        await GiveAudioAsync("n1", "n2", "n3");

        // The longer entry wins in n1, but says what "Helsing" alone already made of it.
        var same = await AppliedAsync(new PronunciationDraft(null, "Van Helsing", "Van HEL-sing"));
        Assert.Empty(same.ParagraphItemIds);
        Assert.Equal(["n1", "n2", "n3"], await WithAudioAsync());

        var different = await AppliedAsync(new PronunciationDraft(same.CreatedId, "Van Helsing", "VAHN HEL-sing"));
        Assert.Equal([_book.ItemId("n1")], different.ParagraphItemIds);
        Assert.Equal(["n2", "n3"], await WithAudioAsync());
    }

    [Fact]
    public async Task ADelete_DiscardsTheAudioOfItsOldMatches_AndEmptiesTheList()
    {
        await SeedAsync("Holmes ran.", "Holmes slept.", "Watson read.");
        var holmes = await AddAsync("Holmes", "Holmz");
        await GiveAudioAsync("n2", "n3");

        var effects = await AppliedAsync(new PronunciationDraft(holmes, null, null, Delete: true));

        Assert.Equal([_book.ItemId("n2")], effects.ParagraphItemIds);
        Assert.Equal(["n3"], await WithAudioAsync());
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task AnEditThatLeavesBothFormsAsTheyAre_ChangesNothing_AndKeepsTheAudio()
    {
        await SeedAsync("Holmes ran.");
        var holmes = await AddAsync("Holmes", "Holmz");
        await GiveAudioAsync("n1");

        var outcome = await CommitAsync(new PronunciationDraft(holmes, " Holmes ", "Holmz"));

        Assert.IsType<BookMutationOutcome.NoChange>(outcome);
        Assert.Equal(["n1"], await WithAudioAsync());
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWrittenFormAnotherEntryHas_IgnoringCase_IsAConflict_AndWritesNothing()
    {
        await SeedAsync("The rain kept falling.");
        await AddAsync("Rain", "RAYN");
        var night = await AddAsync("night", "nite");

        Assert.Equal(BookMutationRejection.Conflict, await RefusedAsync(new PronunciationDraft(null, "RAIN", "rane")));
        Assert.Equal(BookMutationRejection.Conflict, await RefusedAsync(new PronunciationDraft(night, "rain", "rane")));

        Assert.Equal([("night", "nite"), ("Rain", "RAYN")],
            (await ListAsync()).OrderBy(p => p.WrittenForm, StringComparer.OrdinalIgnoreCase).Select(p => (p.WrittenForm, p.SpokenForm)));
    }

    [Theory]
    [InlineData("invalid written", BookMutationRejection.Validation)]
    [InlineData("invalid spoken", BookMutationRejection.Validation)]
    [InlineData("unknown edit", BookMutationRejection.NotFound)]
    [InlineData("unknown delete", BookMutationRejection.NotFound)]
    public async Task AnInvalidFormOrAnUnknownEntry_IsRefused_AndWritesNothing(string draft, BookMutationRejection reason)
    {
        await SeedAsync("Holmes ran.");

        var refused = await RefusedAsync(draft switch
        {
            "invalid written" => new PronunciationDraft(null, "'Holmes", "Holmz"),
            "invalid spoken" => new PronunciationDraft(null, "Holmes", "  "),
            "unknown edit" => new PronunciationDraft(Guid.NewGuid(), "Holmes", "Holmz"),
            _ => new PronunciationDraft(Guid.NewGuid(), null, null, Delete: true),
        });

        Assert.Equal(reason, refused);
        Assert.Empty(await ListAsync());
    }

    // ── harness ─────────────────────────────────────────────────────────────

    /// <summary>One chapter, one narration item per text, named n1, n2, ….</summary>
    private async Task SeedAsync(params string[] narration)
    {
        _book = new BookHierarchyBuilder(OpenDbAsync).AddVolume("vol", v => v.AddChapter("ch", c =>
        {
            for (var n = 1; n <= narration.Length; n++)
            {
                var (name, text) = ($"n{n}", narration[n - 1]);
                c.AddParagraph($"p{n}", p => p.AddNarration(name, text));
            }
        }));
        await _book.BuildAsync();
        _items = narration.Length;
    }

    /// <summary>Arrange-side: the named items have a take, as if generated.</summary>
    private async Task GiveAudioAsync(params string[] items)
    {
        await using var db = await OpenDbAsync();
        foreach (var item in items)
            (await db.ParagraphItems.SingleAsync(i => i.Id == _book.ItemId(item))).AudioFileName = $"{item}.wav";
        await db.SaveChangesAsync();
    }

    /// <summary>The seeded items (n1, n2, …) that still have audio.</summary>
    private async Task<List<string>> WithAudioAsync()
    {
        await using var db = await OpenDbAsync();
        var withAudio = (await db.ParagraphItems.AsNoTracking()
            .Where(i => i.AudioFileName != null && i.AudioFileName != "").Select(i => i.Id).ToListAsync()).ToHashSet();
        return [.. Enumerable.Range(1, _items).Select(n => $"n{n}").Where(n => withAudio.Contains(_book.ItemId(n)))];
    }

    private async Task<List<Read2Me.Data.Entities.Pronunciation>> ListAsync()
    {
        await using var db = await OpenDbAsync();
        return await db.Pronunciations.AsNoTracking().ToListAsync();
    }

    private async Task<BookMutationOutcome> CommitAsync(PronunciationDraft draft)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>()
            .CommitAsync(new SetPronunciationMutation(_folder, draft));
    }

    private async Task<BookMutationEffects> AppliedAsync(PronunciationDraft draft) =>
        Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(draft)).Receipt.Effects;

    private async Task<BookMutationRejection> RefusedAsync(PronunciationDraft draft) =>
        Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(draft)).Reason;

    private async Task<Guid> AddAsync(string written, string spoken) =>
        (await AppliedAsync(new PronunciationDraft(null, written, spoken))).CreatedId!.Value;
}
