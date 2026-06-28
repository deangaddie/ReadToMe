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

    private static string Key(string? prev = null, string? next = null) =>
        OrderKeyGenerator.GenerateKeyBetween(prev, next);

    private static string FloorRank => OrderKeyGenerator.GenerateKeyBetween(null, null);

    // Seeds: 1 Project, 1 Vol → 1 Part → 2 Chapters, each with 1 Para → 1 Item, 1 Narrator, 1 custom char, 2 voices.
    private async Task<(
        Guid VolId, Guid PartId,
        Guid Ch1Id, Guid Para1Id, Guid Item1Id,
        Guid Ch2Id, Guid Para2Id, Guid Item2Id,
        Guid CharId, Guid NarrId,
        Guid VoiceAId, Guid VoiceBId)> SeedBaseAsync(
            ParagraphItemType item1Type = ParagraphItemType.Character,
            ParagraphItemType item2Type = ParagraphItemType.Character,
            bool narratorOnlyMode = false)
    {
        await using var db = await OpenDbAsync();

        db.Projects.Add(new Project
        {
            Title = "T", BookTitle = "B", Author = "A",
            Filename = "f.txt", Type = BookFileType.Text,
            NarratorOnlyMode = narratorOnlyMode
        });

        var charId   = Guid.NewGuid();
        var voiceAId = Guid.NewGuid();
        var voiceBId = Guid.NewGuid();

        db.Characters.Add(new Character { Id = charId, Name = "Alice" });
        db.Voices.Add(new VoiceEntity { Id = voiceAId, CharacterId = charId, Name = "Voice A", Source = VoiceSource.Uploaded, AudioFileName = "a.wav" });
        db.Voices.Add(new VoiceEntity { Id = voiceBId, CharacterId = charId, Name = "Voice B", Source = VoiceSource.Uploaded, AudioFileName = "b.wav" });

        var vol   = new Volume    { Id = Guid.NewGuid(), Title = "V",  Order = Key() };
        var part  = new Part      { Id = Guid.NewGuid(), VolumeId  = vol.Id,  Order = Key() };
        var ch1   = new Chapter   { Id = Guid.NewGuid(), PartId    = part.Id, Order = Key() };
        var ch2   = new Chapter   { Id = Guid.NewGuid(), PartId    = part.Id, Order = Key(ch1.Order) };
        var para1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch1.Id,  Order = Key() };
        var para2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch2.Id,  Order = Key() };

        var item1Id = Guid.NewGuid();
        var item2Id = Guid.NewGuid();
        var narratorId = ProjectDbContext.NarratorId;

        db.Volumes.Add(vol); db.Parts.Add(part);
        db.Chapters.Add(ch1); db.Chapters.Add(ch2);
        db.Paragraphs.Add(para1); db.Paragraphs.Add(para2);

        db.ParagraphItems.Add(new ParagraphItem
        {
            Id = item1Id, ParagraphId = para1.Id, Order = Key(),
            ItemType = item1Type, Text = "Line 1",
            CharacterId = item1Type == ParagraphItemType.Character ? charId : narratorId
        });
        db.ParagraphItems.Add(new ParagraphItem
        {
            Id = item2Id, ParagraphId = para2.Id, Order = Key(),
            ItemType = item2Type, Text = "Line 2",
            CharacterId = item2Type == ParagraphItemType.Character ? charId : narratorId
        });

        await db.SaveChangesAsync();
        return (vol.Id, part.Id, ch1.Id, para1.Id, item1Id, ch2.Id, para2.Id, item2Id,
                charId, narratorId, voiceAId, voiceBId);
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
        var (_, _, _, _, item1Id, _, _, _, charId, _, voiceAId, _) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        var result = await _resolver.ResolveAsync(_folder, [item1Id]);

        Assert.Equal(voiceAId, result[item1Id]);
    }

    // ── 3. Many items, one character ───────────────────────────────────────────

    [Fact]
    public async Task ManyItems_OneCharacter_AllResolveToSameVoice()
    {
        var (_, _, _, _, item1Id, _, _, item2Id, charId, _, voiceAId, _) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        var result = await _resolver.ResolveAsync(_folder, [item1Id, item2Id]);

        Assert.Equal(voiceAId, result[item1Id]);
        Assert.Equal(voiceAId, result[item2Id]);
    }

    // ── 4. Many characters ────────────────────────────────────────────────────

    [Fact]
    public async Task ManyCharacters_EachItemResolvesAgainstItsOwnCharactersRules()
    {
        // item1 → char (Alice), item2 → narrator
        var (_, _, _, _, item1Id, _, _, item2Id, charId, narratorId, voiceAId, voiceBId) =
            await SeedBaseAsync(item2Type: ParagraphItemType.Narration);

        await SeedDefaultRule(charId, voiceAId);     // Alice default → VoiceA
        await SeedDefaultRule(narratorId, voiceBId); // Narrator default → VoiceB

        var result = await _resolver.ResolveAsync(_folder, [item1Id, item2Id]);

        Assert.Equal(voiceAId, result[item1Id]);
        Assert.Equal(voiceBId, result[item2Id]);
    }

    // ── 5. NarratorOnlyMode on ────────────────────────────────────────────────

    [Fact]
    public async Task NarratorOnlyMode_CharacterItem_ResolvesViaNarratorRules()
    {
        var (_, _, _, _, item1Id, _, _, _, charId, narratorId, voiceAId, voiceBId) =
            await SeedBaseAsync(narratorOnlyMode: true);

        await SeedDefaultRule(charId, voiceAId);     // Alice default → VoiceA (should NOT win)
        await SeedDefaultRule(narratorId, voiceBId); // Narrator default → VoiceB

        var result = await _resolver.ResolveAsync(_folder, [item1Id]);

        Assert.Equal(voiceBId, result[item1Id]); // narrator's voice, not Alice's
    }

    // ── 6. Narration item ─────────────────────────────────────────────────────

    [Fact]
    public async Task NarrationItem_AlwaysResolvesViaNarrator()
    {
        var (_, _, _, _, item1Id, _, _, _, charId, narratorId, voiceAId, voiceBId) =
            await SeedBaseAsync(item1Type: ParagraphItemType.Narration);

        await SeedDefaultRule(charId, voiceAId);
        await SeedDefaultRule(narratorId, voiceBId);

        var result = await _resolver.ResolveAsync(_folder, [item1Id]);

        Assert.Equal(voiceBId, result[item1Id]);
    }

    // ── 7. Dangling anchor ────────────────────────────────────────────────────

    [Fact]
    public async Task DanglingAnchor_RuleSkipped_DefaultWins()
    {
        var (_, _, _, _, item1Id, _, _, _, charId, _, voiceAId, voiceBId) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        // Rule with a non-existent chapter id → dangling
        await using var db = await OpenDbAsync();
        db.VoiceRules.Add(new VoiceRule
        {
            Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceBId,
            IsDefault = false, Rank = Key(FloorRank),
            FromLevel = VoiceAnchorLevel.Chapter, FromNodeId = Guid.NewGuid(),
            ToLevel   = VoiceAnchorLevel.Chapter, ToNodeId   = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(_folder, [item1Id]);

        Assert.Equal(voiceAId, result[item1Id]);
    }

    // ── 8. Zero-rules character ───────────────────────────────────────────────

    [Fact]
    public async Task ZeroRulesCharacter_ResolvesToNull()
    {
        var (_, _, _, _, item1Id, _, _, _, _, _, _, _) = await SeedBaseAsync();
        // No rules seeded for any character

        var result = await _resolver.ResolveAsync(_folder, [item1Id]);

        Assert.Null(result[item1Id]);
    }

    // ── 9. ResolveNamesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveNamesAsync_ReturnsCorrectVoiceNames()
    {
        var (_, _, _, _, item1Id, _, _, item2Id, charId, _, voiceAId, _) = await SeedBaseAsync();
        await SeedDefaultRule(charId, voiceAId);

        var result = await _resolver.ResolveNamesAsync(_folder, [item1Id, item2Id]);

        Assert.Equal("Voice A", result[item1Id]);
        Assert.Equal("Voice A", result[item2Id]);
    }

    [Fact]
    public async Task ResolveNamesAsync_NullWhereNoVoice()
    {
        var (_, _, _, _, item1Id, _, _, _, _, _, _, _) = await SeedBaseAsync();
        // No rules → no voice

        var result = await _resolver.ResolveNamesAsync(_folder, [item1Id]);

        Assert.Null(result[item1Id]);
    }

}
