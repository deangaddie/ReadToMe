using System.Data.Common;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Services;
using Read2Me.Services.Books;
using Read2Me.Services.IO;
using Read2Me.Services.Voice;
using Read2Me.Tests.Infrastructure;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services.Voice;

public class VoiceResolverTests : ProjectDbTestBase
{
    private readonly IVoiceResolver _resolver;
    private readonly Read2Me.Core.Models.ProjectFolderId _folder;

    public VoiceResolverTests()
    {
        var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
        var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
        _resolver = new VoiceResolver(session);
        _folder = new Read2Me.Core.Models.ProjectFolderId(FolderName);
    }

    private static string FloorRank => OrderKeyGenerator.GenerateKeyBetween(null, null);

    // Seeds the base book hierarchy: 1 Vol → 1 Part → 2 Chapters, each with 1 Para → 1 Item.
    // Also seeds Alice character + VoiceA + VoiceB.
    // Returns the builder (for named id lookup) + the seeded voice ids.
    private async Task<(BookHierarchyBuilder b, Guid charId, Guid voiceAId, Guid voiceBId)> SeedBaseAsync(
        ParagraphItemType item1Type = ParagraphItemType.Character,
        ParagraphItemType item2Type = ParagraphItemType.Character,
        bool narratorOnlyMode = false,
        bool linkNarratorToAlice = false,
        Guid? danglingNarratorLink = null)
    {
        var charId   = Guid.NewGuid();
        var voiceAId = Guid.NewGuid();
        var voiceBId = Guid.NewGuid();
        var alice    = new Character { Id = charId, Name = "Alice" };

        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithProject(narratorOnlyMode: narratorOnlyMode);
        b.WithCharacter("alice", alice);
        if (linkNarratorToAlice) b.WithNarratorLink(charId);
        else if (danglingNarratorLink is { } dangling) b.WithNarratorLink(dangling);

        await b
            .AddVolume("vol", v => v
                .AddChapter("ch1", c => c
                    .AddParagraph(configure: p =>
                    {
                        if (item1Type == ParagraphItemType.Narration) p.AddNarration("item1", "Line 1");
                        else p.AddCharacterLine("item1", "Line 1", speaker: "alice");
                    }))
                .AddChapter("ch2", c => c
                    .AddParagraph(configure: p =>
                    {
                        if (item2Type == ParagraphItemType.Narration) p.AddNarration("item2", "Line 2");
                        else p.AddCharacterLine("item2", "Line 2", speaker: "alice");
                    })))
            .BuildAsync();

        // Seed voices via a separate context (builder doesn't own Voice)
        await using var db = await OpenDbAsync();
        db.Voices.Add(new VoiceEntity { Id = voiceAId, CharacterId = charId, Name = "Voice A", Source = VoiceSource.Uploaded, AudioFileName = "a.wav" });
        db.Voices.Add(new VoiceEntity { Id = voiceBId, CharacterId = charId, Name = "Voice B", Source = VoiceSource.Uploaded, AudioFileName = "b.wav" });
        await db.SaveChangesAsync();

        return (b, charId, voiceAId, voiceBId);
    }

    private async Task SeedDefaultRule(Guid charId, Guid voiceId)
    {
        await using var db = await OpenDbAsync();
        db.VoiceRules.Add(new VoiceRule
        {
            Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId,
            IsDefault = true, Rank = FloorRank
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedChapterRule(Guid charId, Guid voiceId, string rank, Guid chapterId, bool fromHereOn = false)
    {
        await using var db = await OpenDbAsync();
        db.VoiceRules.Add(new VoiceRule
        {
            Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId,
            IsDefault = false, Rank = rank,
            FromLevel = VoiceAnchorLevel.Chapter, FromNodeId = chapterId,
            ToLevel   = fromHereOn ? null : VoiceAnchorLevel.Chapter,
            ToNodeId  = fromHereOn ? null : chapterId,
        });
        await db.SaveChangesAsync();
    }

    // ── 1. Empty set ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyItemIds_ReturnsEmptyDictionary()
    {
        var result = await _resolver.ResolveAsync(_folder, Array.Empty<Guid>());
        Assert.Empty(result);
    }

    // ── 2. Single Character item, default rule ─────────────────────────────────

    [Fact]
    public async Task SingleCharacterItem_DefaultRule_ResolvesToDefaultVoice()
    {
        var (b, charId, voiceAId, _) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Equal(voiceAId, result[b.ItemId("item1")]);
    }

    // ── 3. Many items, one character ───────────────────────────────────────────

    [Fact]
    public async Task ManyItems_OneCharacter_AllResolveToSameVoice()
    {
        var (b, charId, voiceAId, _) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1"), b.ItemId("item2")]);

        Assert.Equal(voiceAId, result[b.ItemId("item1")]);
        Assert.Equal(voiceAId, result[b.ItemId("item2")]);
    }

    // ── 4. Many characters ────────────────────────────────────────────────────

    [Fact]
    public async Task ManyCharacters_EachItemResolvesAgainstItsOwnCharactersRules()
    {
        // item1 → Alice (Character), item2 → Narrator
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(item2Type: ParagraphItemType.Narration);
        var narratorId = ProjectDbContext.NarratorId;

        await SeedDefaultRule(charId, voiceAId);     // Alice default → VoiceA
        await SeedDefaultRule(narratorId, voiceBId); // Narrator default → VoiceB

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1"), b.ItemId("item2")]);

        Assert.Equal(voiceAId, result[b.ItemId("item1")]);
        Assert.Equal(voiceBId, result[b.ItemId("item2")]);
    }

    // ── 5. NarratorOnlyMode on ────────────────────────────────────────────────

    [Fact]
    public async Task NarratorOnlyMode_CharacterItem_ResolvesViaNarratorRules()
    {
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(narratorOnlyMode: true);
        var narratorId = ProjectDbContext.NarratorId;

        await SeedDefaultRule(charId, voiceAId);     // Alice → VoiceA (should NOT win)
        await SeedDefaultRule(narratorId, voiceBId); // Narrator → VoiceB

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Equal(voiceBId, result[b.ItemId("item1")]);
    }

    // ── 6. Narration item ─────────────────────────────────────────────────────

    [Fact]
    public async Task NarrationItem_AlwaysResolvesViaNarrator()
    {
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(item1Type: ParagraphItemType.Narration);
        var narratorId = ProjectDbContext.NarratorId;

        await SeedDefaultRule(charId, voiceAId);
        await SeedDefaultRule(narratorId, voiceBId);

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Equal(voiceBId, result[b.ItemId("item1")]);
    }

    // ── 7. Zero-rules character ───────────────────────────────────────────────

    [Fact]
    public async Task ZeroRulesCharacter_ResolvesToNull()
    {
        var (b, _, _, _) = await SeedBaseAsync();

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Null(result[b.ItemId("item1")]);
    }

    // ── 8. ResolveNamesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveNamesAsync_ReturnsCorrectVoiceNames()
    {
        var (b, charId, voiceAId, _) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        var result = await _resolver.ResolveNamesAsync(_folder, [b.ItemId("item1"), b.ItemId("item2")]);

        Assert.Equal("Voice A", result[b.ItemId("item1")]);
        Assert.Equal("Voice A", result[b.ItemId("item2")]);
    }

    [Fact]
    public async Task ResolveNamesAsync_NullWhereNoVoice()
    {
        var (b, _, _, _) = await SeedBaseAsync();

        var result = await _resolver.ResolveNamesAsync(_folder, [b.ItemId("item1")]);

        Assert.Null(result[b.ItemId("item1")]);
    }

    // ── 9. Narrator link (slice 13) ───────────────────────────────────────────

    [Fact]
    public async Task Linked_NarrationItem_ResolvesViaLinkedCharactersDefaultVoice()
    {
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(
            item1Type: ParagraphItemType.Narration, linkNarratorToAlice: true);
        await SeedDefaultRule(charId, voiceAId);                       // Alice → VoiceA
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceBId);  // seed Narrator → VoiceB (must NOT win)

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Equal(voiceAId, result[b.ItemId("item1")]);
    }

    [Fact]
    public async Task Linked_NarrationItem_PositionalRuleOnLinkedCharacterWins()
    {
        // The linked character's *rules* are genuinely evaluated, not just its default voice.
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(
            item1Type: ParagraphItemType.Narration, linkNarratorToAlice: true);
        await SeedDefaultRule(charId, voiceBId);                                   // default → VoiceB
        await SeedChapterRule(charId, voiceAId, FloorRank, b.ChapterId("ch1"));    // ch1 → VoiceA

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1"), b.ItemId("item2")]);

        Assert.Equal(voiceAId, result[b.ItemId("item1")]);  // narration in ch1 → positional rule
        Assert.Equal(voiceBId, result[b.ItemId("item2")]);  // Alice's dialog in ch2 → default
    }

    [Fact]
    public async Task Linked_NarratorOnlyMode_EveryItemResolvesToLinkedCharacter()
    {
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(
            item1Type: ParagraphItemType.Narration, narratorOnlyMode: true, linkNarratorToAlice: true);
        await SeedDefaultRule(charId, voiceAId);
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceBId);

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1"), b.ItemId("item2")]);

        Assert.Equal(voiceAId, result[b.ItemId("item1")]);
        Assert.Equal(voiceAId, result[b.ItemId("item2")]);
    }

    [Fact]
    public async Task Linked_DanglingLink_FallsBackToSeedNarratorRow()
    {
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(
            item1Type: ParagraphItemType.Narration, danglingNarratorLink: Guid.NewGuid());
        await SeedDefaultRule(charId, voiceAId);
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceBId);

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Equal(voiceBId, result[b.ItemId("item1")]);
    }

    // ── 10. The speaker decides, not the item type (ADR-0006) ────────────────

    [Fact]
    public async Task Flip_NarrationItemToCharacterAndBack_ResolvedVoiceFollows()
    {
        // The whole point of the collapse, at the seam a user actually reaches: stamp a speaker
        // on a narration item and it is read in that speaker's voice; stamp the narrator back and
        // narration's own voice returns.
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(item1Type: ParagraphItemType.Narration);
        await SeedDefaultRule(charId, voiceAId);                       // Alice → Voice A
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceBId);  // Narrator → Voice B
        var commands = NewCommandHandler();
        var itemId = b.ItemId("item1");

        Assert.Equal(voiceBId, (await _resolver.ResolveAsync(_folder, [itemId]))[itemId]);

        await commands.ExecuteAsync(new SetItemCharacterCommand(_folder, itemId, charId));
        Assert.Equal(voiceAId, (await _resolver.ResolveAsync(_folder, [itemId]))[itemId]);

        await commands.ExecuteAsync(new SetItemCharacterCommand(_folder, itemId, ProjectDbContext.NarratorId));
        Assert.Equal(voiceBId, (await _resolver.ResolveAsync(_folder, [itemId]))[itemId]);
    }

    [Fact]
    public async Task Linked_ItemStampedWithLinkedCharacter_ResolvesViaThatCharactersOwnRules()
    {
        // Under a narrator link, "Narrator (Alice)" and "Alice" are different stored speakers.
        // Alice's dialog runs through Alice's rules; the seed Narrator's rules never reach it.
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(
            item1Type: ParagraphItemType.Narration, linkNarratorToAlice: true);
        await SeedChapterRule(charId, voiceAId, FloorRank, b.ChapterId("ch2"));  // Alice: ch2 → Voice A
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceBId);            // seed Narrator → Voice B

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item2")]);

        Assert.Equal(voiceAId, result[b.ItemId("item2")]);
    }

    [Fact]
    public async Task NarratorOnlyMode_StampedCharacterIsOverriddenButNotChanged()
    {
        var (b, charId, voiceAId, voiceBId) = await SeedBaseAsync(narratorOnlyMode: true);
        await SeedDefaultRule(charId, voiceAId);
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceBId);

        var result = await _resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        Assert.Equal(voiceBId, result[b.ItemId("item1")]);
        await using var verify = await OpenDbAsync();
        Assert.Equal(charId, (await verify.ParagraphItems.FindAsync(b.ItemId("item1")))!.CharacterId);
    }

    private BookCommandHandler NewCommandHandler()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        return services.BuildServiceProvider().GetRequiredService<BookCommandHandler>();
    }

    [Fact]
    public async Task Linked_PaysNoExtraRoundTrip()
    {
        // The link rides the Projects query already made for NarratorOnlyMode. The counts are
        // absolute, not just equal: an unconditional extra read would keep them equal but move
        // both off the baseline.
        var unlinked = await CountResolveCommandsAsync(link: false);
        var linked   = await CountResolveCommandsAsync(link: true);

        Assert.Equal(ResolveRoundTrips, unlinked);
        Assert.Equal(ResolveRoundTrips, linked);
    }

    /// <summary>
    /// Round-trips one <see cref="IVoiceResolver.ResolveAsync"/> over a single narration item
    /// costs: items+ancestry, project narration settings, the character's rules.
    /// </summary>
    private const int ResolveRoundTrips = 3;

    private async Task<int> CountResolveCommandsAsync(bool link)
    {
        var (b, charId, voiceAId, _) = await SeedBaseAsync(
            item1Type: ParagraphItemType.Narration, linkNarratorToAlice: link);
        await SeedDefaultRule(charId, voiceAId);
        await SeedDefaultRule(ProjectDbContext.NarratorId, voiceAId);

        var counter = new CommandCountingInterceptor();
        var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
        await using var session = new ProjectDbSession(
            fs, new CountingDbContextFactory(counter), NullLogger<ProjectDbSession>.Instance);
        var resolver = new VoiceResolver(session);

        // Warm the session so migration commands do not land in the count.
        await resolver.ResolveAsync(_folder, [b.ItemId("item1")]);
        counter.Reset();
        await resolver.ResolveAsync(_folder, [b.ItemId("item1")]);

        return counter.Count;
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;
        public void Reset() => _count = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingDbContextFactory(DbCommandInterceptor interceptor) : IProjectDbContextFactory
    {
        public async Task<ProjectDbContext> CreateAsync(string folderPath)
        {
            Directory.CreateDirectory(folderPath);
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(folderPath, "project.db")};Pooling=false")
                .AddInterceptors(interceptor)
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }
    }
}
