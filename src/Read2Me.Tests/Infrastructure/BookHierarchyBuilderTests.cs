using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Infrastructure;

public class BookHierarchyBuilderTests : ProjectDbTestBase
{
    // ── 1. Tracer bullet ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddVolume_BuildAsync_WritesVolumeToDb()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);

        await b.AddVolume("Vol 1").BuildAsync();

        await using var db = await OpenDbAsync();
        Assert.Single(await db.Volumes.ToListAsync());
    }

    // ── 2. Named lookup / AddNarration ───────────────────────────────────────

    [Fact]
    public async Task NamedLookup_ReturnsCorrectIdsForAllLevels()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v
            .AddChapter("ch", c => c
                .AddParagraph("para", p => p
                    .AddNarration("item", "Hello"))))
          .BuildAsync();

        await using var db = await OpenDbAsync();

        var vol  = await db.Volumes.FindAsync(b.VolumeId("vol"));
        var ch   = await db.Chapters.FindAsync(b.ChapterId("ch"));
        var para = await db.Paragraphs.FindAsync(b.ParagraphId("para"));
        var item = await db.ParagraphItems.FindAsync(b.ItemId("item"));

        Assert.NotNull(vol);
        Assert.NotNull(ch);
        Assert.NotNull(para);
        Assert.NotNull(item);
    }

    [Fact]
    public async Task AddNarration_SetsNarratorIdAndNarrationItemType()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v", v => v
            .AddChapter(configure: c => c
                .AddParagraph(configure: p => p
                    .AddNarration("n1", "Hello world"))))
          .BuildAsync();

        await using var db = await OpenDbAsync();
        var item = await db.ParagraphItems.FindAsync(b.ItemId("n1"));

        Assert.NotNull(item);
        Assert.Equal(ParagraphItemType.Speech, item.ItemType);
        Assert.Equal(ProjectDbContext.NarratorId, item.CharacterId);
        Assert.Equal("Hello world", item.Text);
    }

    // ── 3. AddCharacterLine ───────────────────────────────────────────────────

    [Fact]
    public async Task AddCharacterLine_SetsCharacterItemTypeAndRegisteredSpeakerId()
    {
        var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithCharacter("alice", alice);
        await b.AddVolume("v", v => v
            .AddChapter(configure: c => c
                .AddParagraph(configure: p => p
                    .AddCharacterLine("l1", "Hi there", speaker: "alice"))))
          .BuildAsync();

        await using var db = await OpenDbAsync();
        var item = await db.ParagraphItems.FindAsync(b.ItemId("l1"));

        Assert.NotNull(item);
        Assert.Equal(ParagraphItemType.Speech, item.ItemType);
        Assert.Equal(alice.Id, item.CharacterId);
        Assert.Equal("Hi there", item.Text);
    }

    // ── 4. Sibling order keys strictly ascending ──────────────────────────────

    [Fact]
    public async Task SiblingChapters_HaveStrictlyAscendingOrderKeys()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v", v => v
            .AddChapter("ch1", c => c.AddParagraph(configure: p => p.AddNarration("n1", "a")))
            .AddChapter("ch2", c => c.AddParagraph(configure: p => p.AddNarration("n2", "b"))))
          .BuildAsync();

        await using var db = await OpenDbAsync();
        var ch1 = await db.Chapters.FindAsync(b.ChapterId("ch1"));
        var ch2 = await db.Chapters.FindAsync(b.ChapterId("ch2"));

        Assert.NotNull(ch1); Assert.NotNull(ch2);
        Assert.True(string.Compare(ch1.Order, ch2.Order, StringComparison.Ordinal) < 0,
            $"ch1.Order '{ch1.Order}' should be < ch2.Order '{ch2.Order}'");
    }

    // ── 5. WithProject ────────────────────────────────────────────────────────

    [Fact]
    public async Task WithProject_NarratorOnlyMode_PersistsFlag()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithProject(narratorOnlyMode: true);
        await b.AddVolume("v").BuildAsync();

        await using var db = await OpenDbAsync();
        var project = await db.Projects.FirstAsync();
        Assert.True(project.NarratorOnlyMode);
    }
}
