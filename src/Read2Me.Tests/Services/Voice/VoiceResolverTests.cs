using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
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
        bool narratorOnlyMode = false)
    {
        var charId   = Guid.NewGuid();
        var voiceAId = Guid.NewGuid();
        var voiceBId = Guid.NewGuid();
        var alice    = new Character { Id = charId, Name = "Alice" };

        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithProject(narratorOnlyMode: narratorOnlyMode);
        b.WithCharacter("alice", alice);

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
}
