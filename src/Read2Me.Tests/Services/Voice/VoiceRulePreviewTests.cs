using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
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

/// <summary>
/// The resolved-voice preview behind the cast page's Voice rules section (Angular ticket 17): the
/// voice a character's rules pick at the start of every chapter, in book order.
/// </summary>
public class VoiceRulePreviewTests : ProjectDbTestBase
{
    private readonly VoiceRulePreview _preview;
    private readonly ProjectFolderId _folder;

    public VoiceRulePreviewTests()
    {
        var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
        var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
        _preview = new VoiceRulePreview(session);
        _folder = new ProjectFolderId(FolderName);
    }

    private static string FloorRank => OrderKeyGenerator.GenerateKeyBetween(null, null);

    /// <summary>Two volumes, three chapters (v1: ch1, ch2; v2: ch3), Alice with Voice A and Voice B.</summary>
    private async Task<(BookHierarchyBuilder b, Guid charId, Guid voiceAId, Guid voiceBId)> SeedBaseAsync()
    {
        var charId   = Guid.NewGuid();
        var voiceAId = Guid.NewGuid();
        var voiceBId = Guid.NewGuid();

        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithProject();
        b.WithCharacter("alice", new Character { Id = charId, Name = "Alice" });
        await b
            .AddVolume("v1", v => v
                .AddChapter("ch1", c => c.AddParagraph(configure: p => p.AddCharacterLine("i1", "Line 1", speaker: "alice")))
                .AddChapter("ch2", c => c.AddParagraph(configure: p => p.AddCharacterLine("i2", "Line 2", speaker: "alice"))))
            .AddVolume("v2", v => v
                .AddChapter("ch3", c => c.AddParagraph(configure: p => p.AddCharacterLine("i3", "Line 3", speaker: "alice"))))
            .BuildAsync();

        await using var db = await OpenDbAsync();
        db.Voices.Add(new VoiceEntity { Id = voiceAId, CharacterId = charId, Name = "Voice A", Source = VoiceSource.Uploaded, AudioFileName = "a.wav" });
        db.Voices.Add(new VoiceEntity { Id = voiceBId, CharacterId = charId, Name = "Voice B", Source = VoiceSource.Uploaded, AudioFileName = "b.wav" });
        await db.SaveChangesAsync();

        return (b, charId, voiceAId, voiceBId);
    }

    private async Task SeedRuleAsync(Guid charId, Guid voiceId, string rank, bool isDefault = false,
        VoiceAnchorLevel? fromLevel = null, Guid? fromNodeId = null,
        VoiceAnchorLevel? toLevel = null, Guid? toNodeId = null)
    {
        await using var db = await OpenDbAsync();
        db.VoiceRules.Add(new VoiceRule
        {
            Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId,
            IsDefault = isDefault, Rank = rank,
            FromLevel = fromLevel, FromNodeId = fromNodeId, ToLevel = toLevel, ToNodeId = toNodeId,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task No_rules_lists_every_chapter_in_book_order_with_no_voice()
    {
        var (b, charId, _, _) = await SeedBaseAsync();

        var rows = await _preview.PreviewChaptersAsync(_folder, charId);

        Assert.Equal([b.ChapterId("ch1"), b.ChapterId("ch2"), b.ChapterId("ch3")], rows.Select(r => r.ChapterId));
        Assert.Equal(["ch1", "ch2", "ch3"], rows.Select(r => r.ChapterTitle));
        Assert.All(rows, r => Assert.Null(r.VoiceName));
    }

    [Fact]
    public async Task From_chapter_onward_flips_the_voice_at_that_chapter()
    {
        var (b, charId, voiceA, voiceB) = await SeedBaseAsync();
        await SeedRuleAsync(charId, voiceA, FloorRank, isDefault: true);
        await SeedRuleAsync(charId, voiceB, OrderKeyGenerateAfter(FloorRank),
            fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: b.ChapterId("ch2"));

        var rows = await _preview.PreviewChaptersAsync(_folder, charId);

        Assert.Equal(["Voice A", "Voice B", "Voice B"], rows.Select(r => r.VoiceName));
    }

    [Fact]
    public async Task Just_this_chapter_returns_to_the_default_afterwards()
    {
        var (b, charId, voiceA, voiceB) = await SeedBaseAsync();
        await SeedRuleAsync(charId, voiceA, FloorRank, isDefault: true);
        await SeedRuleAsync(charId, voiceB, OrderKeyGenerateAfter(FloorRank),
            fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: b.ChapterId("ch2"),
            toLevel: VoiceAnchorLevel.Chapter, toNodeId: b.ChapterId("ch2"));

        var rows = await _preview.PreviewChaptersAsync(_folder, charId);

        Assert.Equal(["Voice A", "Voice B", "Voice A"], rows.Select(r => r.VoiceName));
    }

    [Fact]
    public async Task Volume_anchor_covers_every_chapter_of_that_volume()
    {
        var (b, charId, voiceA, voiceB) = await SeedBaseAsync();
        await SeedRuleAsync(charId, voiceA, FloorRank, isDefault: true);
        await SeedRuleAsync(charId, voiceB, OrderKeyGenerateAfter(FloorRank),
            fromLevel: VoiceAnchorLevel.Volume, fromNodeId: b.VolumeId("v2"),
            toLevel: VoiceAnchorLevel.Volume, toNodeId: b.VolumeId("v2"));

        var rows = await _preview.PreviewChaptersAsync(_folder, charId);

        Assert.Equal(["Voice A", "Voice A", "Voice B"], rows.Select(r => r.VoiceName));
    }

    [Fact]
    public async Task Paragraph_anchor_inside_a_chapter_does_not_change_the_chapter_start()
    {
        var (b, charId, voiceA, voiceB) = await SeedBaseAsync();
        await SeedRuleAsync(charId, voiceA, FloorRank, isDefault: true);
        await SeedRuleAsync(charId, voiceB, OrderKeyGenerateAfter(FloorRank),
            fromLevel: VoiceAnchorLevel.ParagraphItem, fromNodeId: b.ItemId("i2"),
            toLevel: VoiceAnchorLevel.ParagraphItem, toNodeId: b.ItemId("i2"));

        var rows = await _preview.PreviewChaptersAsync(_folder, charId);

        // The preview is per chapter start; an item-scoped rule inside ch2 is below its grain.
        Assert.Equal(["Voice A", "Voice A", "Voice A"], rows.Select(r => r.VoiceName));
    }

    [Fact]
    public async Task Dangling_rule_is_skipped()
    {
        var (_, charId, voiceA, voiceB) = await SeedBaseAsync();
        await SeedRuleAsync(charId, voiceA, FloorRank, isDefault: true);
        await SeedRuleAsync(charId, voiceB, OrderKeyGenerateAfter(FloorRank),
            fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: Guid.NewGuid());

        var rows = await _preview.PreviewChaptersAsync(_folder, charId);

        Assert.Equal(["Voice A", "Voice A", "Voice A"], rows.Select(r => r.VoiceName));
    }

    [Fact]
    public async Task Unknown_character_lists_chapters_with_no_voice()
    {
        var (_, charId, voiceA, _) = await SeedBaseAsync();
        await SeedRuleAsync(charId, voiceA, FloorRank, isDefault: true);

        var rows = await _preview.PreviewChaptersAsync(_folder, Guid.NewGuid());

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Null(r.VoiceName));
    }

    private static string OrderKeyGenerateAfter(string rank) => OrderKeyGenerator.GenerateKeyBetween(rank, null);
}
