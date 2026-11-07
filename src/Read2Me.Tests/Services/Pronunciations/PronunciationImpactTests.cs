using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.Pronunciations;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

/// <summary>
/// The dry run of a list change, <see cref="PronunciationImpact.ReadAsync"/>, over a real project: its
/// reach, the audio a save would discard (exactly what <see cref="SetPronunciationMutation"/> then
/// discards), the entries it overlaps or duplicates, and each match in its sentence, written and spoken.
/// </summary>
public class PronunciationImpactTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private BookHierarchyBuilder _book = null!;

    public PronunciationImpactTests()
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

    [Theory]
    [InlineData("add", 1)]
    [InlineData("spoken", 1)]
    [InlineData("written", 2)]
    [InlineData("overlap", 1)]
    [InlineData("delete", 1)]
    public async Task TheAudioADraftWouldDiscard_IsWhatSavingItThenDiscards(string change, int discarded)
    {
        await SeedAsync("Holmes ran. Holmes fell.", "Holmes slept.", "Watson read.", "Lestrade sang.");
        var holmes = await AddAsync("Holmes", "Holmz");
        await GiveAudioAsync("n1", "n3", "n4");
        var draft = change switch
        {
            "add" => new PronunciationDraft(null, "Watson", "WAT-sun"),
            "spoken" => new PronunciationDraft(holmes, "Holmes", "Hohmz"),
            "written" => new PronunciationDraft(holmes, "Lestrade", "Holmz"),
            "overlap" => new PronunciationDraft(null, "Holmes ran", "Holmz runs"), // wins over "Holmes" in n1 only
            _ => new PronunciationDraft(holmes, null, null, Delete: true),
        };

        var impact = await ImpactAsync(draft);

        Assert.Equal(discarded, impact.AudioDiscardCount);
        Assert.Equal(["Holmes"], (await ListAsync()).Select(p => p.WrittenForm)); // a dry run writes nothing
        var saved = Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(draft)).Receipt.Effects;
        Assert.Equal(discarded, saved.ParagraphItemIds.Count);
    }

    [Fact]
    public async Task ADraft_CountsItsReach_AndNamesEachMatchInItsSentence_WrittenAndSpoken()
    {
        await SeedAsync("The rain fell. Van Helsing stood.", "Helsing slept. Watson read.", "Nothing here.");
        var vanHelsing = await AddAsync("Van Helsing", "VAHN HEL-sing");
        await AddAsync("Watson", "WAT-sun");
        await GiveAudioAsync("n2");

        var impact = await ImpactAsync(new PronunciationDraft(null, "helsing", "HEL-sing"));

        // "Van Helsing" wins in n1, so the draft's only match is n2's.
        Assert.Equal((1, 1, 1, 1), (impact.MatchCount, impact.ItemCount, impact.WithAudioCount, impact.AudioDiscardCount));
        Assert.Null(impact.DuplicateOf);
        Assert.Equal([new PronunciationOverlap(vanHelsing, "Van Helsing", "VAHN HEL-sing", DraftWins: false)], impact.Overlaps);
        Assert.Equal(
            [new PronunciationMatchInBook(_book.ItemId("n2"), _book.ChapterId("ch"), "ch", ProjectDbContext.NarratorName, HasAudio: true,
                "Helsing slept.", SentenceStart: 0, MatchStart: 0, MatchLength: 7,
                "HEL-sing slept.", SpokenMatchStart: 0, SpokenMatchLength: 8)],
            impact.Matches);
    }

    [Fact]
    public async Task AMatchInALaterSentence_NamesWhereThatSentenceStarts_AndRespeltsTheWholeSentence()
    {
        await SeedAsync("Helsing slept. Watson read.");
        await AddAsync("Watson", "WAT-sun");

        var match = Assert.Single((await ImpactAsync(new PronunciationDraft(null, "read", "red"))).Matches);

        Assert.Equal(("Watson read.", 15, 7, 4), (match.Sentence, match.SentenceStart, match.MatchStart, match.MatchLength));
        Assert.Equal(("WAT-sun red.", 8, 3), (match.SpokenSentence, match.SpokenMatchStart, match.SpokenMatchLength));
    }

    [Fact]
    public async Task TheMatchsCase_CarriesOverIntoTheSpokenSentence()
    {
        await SeedAsync("Watson read.");
        var watson = await AddAsync("Watson", "WAT-sun");

        var match = Assert.Single((await ImpactAsync(new PronunciationDraft(watson, "Watson", "wot-son"))).Matches);

        Assert.Equal("Wot-son read.", match.SpokenSentence);
        Assert.Equal("Wot-son", match.SpokenSentence.Substring(match.SpokenMatchStart, match.SpokenMatchLength));
    }

    [Fact]
    public async Task ADeletesReach_IsWhereTheEntryMatchesNow_AndItsSentenceIsSpokenWithoutIt()
    {
        await SeedAsync("Watson read.", "Watson wrote.");
        var watson = await AddAsync("Watson", "WAT-sun");
        await GiveAudioAsync("n2");

        var impact = await ImpactAsync(new PronunciationDraft(watson, null, null, Delete: true));

        Assert.Equal((2, 2, 1, 1), (impact.MatchCount, impact.ItemCount, impact.WithAudioCount, impact.AudioDiscardCount));
        Assert.Equal(["Watson read.", "Watson wrote."], impact.Matches.Select(m => m.SpokenSentence));
    }

    [Fact]
    public async Task Overlaps_NameTheEntriesThatHoldTheDraftOrThatItHolds_AndWhichWins()
    {
        await SeedAsync("Van Helsing Senior came.");
        var helsing = await AddAsync("Helsing", "HEL-sing");
        var senior = await AddAsync("Van Helsing Senior", "VAHN HEL-sing SEEN-yor");

        var impact = await ImpactAsync(new PronunciationDraft(null, "Van Helsing", "VAHN HEL-sing"));

        Assert.Equal(
            [
                new PronunciationOverlap(helsing, "Helsing", "HEL-sing", DraftWins: true),
                new PronunciationOverlap(senior, "Van Helsing Senior", "VAHN HEL-sing SEEN-yor", DraftWins: false),
            ],
            impact.Overlaps);
    }

    [Fact]
    public async Task ADraftOfAWrittenFormAnotherEntryHas_IgnoringCase_NamesThatEntry_AndCountsAsIfInItsPlace()
    {
        await SeedAsync("Watson read.");
        var watson = await AddAsync("Watson", "WAT-sun");
        await GiveAudioAsync("n1");

        var impact = await ImpactAsync(new PronunciationDraft(null, "WATSON", "WAT-sun"));

        Assert.Equal(new PronunciationRef(watson, "Watson", "WAT-sun"), impact.DuplicateOf);
        Assert.Equal((1, 0), (impact.MatchCount, impact.AudioDiscardCount)); // saying what "Watson" says already
    }

    // ── harness ─────────────────────────────────────────────────────────────

    /// <summary>One chapter "ch", one narration item per text, named n1, n2, ….</summary>
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
    }

    /// <summary>Arrange-side: the named items have a take, as if generated.</summary>
    private async Task GiveAudioAsync(params string[] items)
    {
        await using var db = await OpenDbAsync();
        foreach (var item in items)
            (await db.ParagraphItems.SingleAsync(i => i.Id == _book.ItemId(item))).AudioFileName = $"{item}.wav";
        await db.SaveChangesAsync();
    }

    private async Task<PronunciationImpactReport> ImpactAsync(PronunciationDraft draft)
    {
        await using var db = await OpenDbAsync();
        return await PronunciationImpact.ReadAsync(db, draft, TestContext.Current.CancellationToken);
    }

    private async Task<List<Pronunciation>> ListAsync()
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

    private async Task<Guid> AddAsync(string written, string spoken) =>
        Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(new PronunciationDraft(null, written, spoken)))
            .Receipt.Effects.CreatedId!.Value;
}
