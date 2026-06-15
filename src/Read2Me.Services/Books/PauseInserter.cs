using FractionalIndexing;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.Services.Books;

internal static class PauseInserter
{
    public static Paragraph AddPauseParagraph(
        ProjectDbContext db, Guid chapterId, ParagraphItemType pauseType,
        string? afterOrder, string? beforeOrder)
    {
        var para = new Paragraph
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Order = OrderKeyGenerator.GenerateKeyBetween(afterOrder, beforeOrder),
        };
        db.Paragraphs.Add(para);
        db.ParagraphItems.Add(new ParagraphItem
        {
            Id = Guid.NewGuid(),
            ParagraphId = para.Id,
            ItemType = pauseType,
            Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
        });
        return para;
    }
}
