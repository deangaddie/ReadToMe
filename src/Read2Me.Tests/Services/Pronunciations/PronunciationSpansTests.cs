using Read2Me.Data.Entities;
using Read2Me.Services.Pronunciations;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Pronunciations;

/// <summary>
/// The Book view's marks as <see cref="PronunciationSpans.ReadAsync"/> reads them over a real project:
/// each span names its entry and where the sentence around it starts, the longest written form winning.
/// </summary>
public class PronunciationSpansTests : ProjectDbTestBase
{
    [Fact]
    public async Task ASpan_NamesTheSentenceItIsIn_AndTheLongestWrittenFormWins()
    {
        var book = new BookHierarchyBuilder(OpenDbAsync).AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("p1", p => p.AddNarration("n1", "Holmes ran. Van Helsing fell."))));
        await book.BuildAsync();
        var (holmes, vanHelsing, helsing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var db = await OpenDbAsync())
        {
            db.Pronunciations.AddRange(
                new Pronunciation { Id = holmes, WrittenForm = "Holmes", SpokenForm = "HOHMZ" },
                new Pronunciation { Id = vanHelsing, WrittenForm = "Van Helsing", SpokenForm = "VAHN HEL-sing" },
                new Pronunciation { Id = helsing, WrittenForm = "Helsing", SpokenForm = "HEL-sing" });
            await db.SaveChangesAsync();
        }

        await using var read = await OpenDbAsync();
        var report = await PronunciationSpans.ReadAsync(read, [book.ChapterId("ch")], TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new PronunciationSpan(book.ItemId("n1"), 0, 6, holmes, SentenceStart: 0),
                new PronunciationSpan(book.ItemId("n1"), 12, 11, vanHelsing, SentenceStart: 12),
            ],
            report.Spans);
        Assert.Equal(["Holmes", "Van Helsing"], report.Pronunciations.Select(p => p.WrittenForm)); // only the entries a span names
    }
}
