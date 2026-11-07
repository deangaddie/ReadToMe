using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Entities;
using Read2Me.Services.Pronunciations;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

/// <summary>
/// The list as <see cref="PronunciationList.ReadAsync"/> reads it over a real project: sorted by
/// written form, each entry with its reach — its matches, the items holding one, and those with audio.
/// </summary>
public class PronunciationListTests : ProjectDbTestBase
{
    [Fact]
    public async Task EachEntry_CountsItsMatches_TheItemsHoldingOne_AndThoseWithAudio_SortedByWrittenForm()
    {
        var book = new BookHierarchyBuilder(OpenDbAsync).AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("p1", p => p.AddNarration("n1", "Holmes ran. Holmes fell."))
            .AddParagraph("p2", p => p.AddNarration("n2", "Holmes slept."))
            .AddParagraph("p3", p => p.AddNarration("n3", "Watson read."))));
        await book.BuildAsync();
        await using (var db = await OpenDbAsync())
        {
            db.Pronunciations.AddRange(
                new Pronunciation { Id = Guid.NewGuid(), WrittenForm = "Holmes", SpokenForm = "Holmz" },
                new Pronunciation { Id = Guid.NewGuid(), WrittenForm = "absent", SpokenForm = "AB-sent" },
                new Pronunciation { Id = Guid.NewGuid(), WrittenForm = "Watson", SpokenForm = "WAT-sun" });
            (await db.ParagraphItems.SingleAsync(i => i.Id == book.ItemId("n2"))).AudioFileName = "n2.wav";
            await db.SaveChangesAsync();
        }

        await using var read = await OpenDbAsync();
        var list = await PronunciationList.ReadAsync(read, TestContext.Current.CancellationToken);

        Assert.Equal(
            [("absent", 0, 0, 0), ("Holmes", 3, 2, 1), ("Watson", 1, 1, 0)],
            list.Select(e => (e.WrittenForm, e.MatchCount, e.ItemCount, e.WithAudioCount)));
    }
}
