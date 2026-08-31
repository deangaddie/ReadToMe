using Read2Me.Core.Utils;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.Services.Books;

internal static class TitleInserter
{
    // Inserts a narration paragraph ordered before `beforeOrder` (or first if null).
    public static Paragraph AddTitleParagraph(
        ProjectDbContext db, Guid chapterId, string text, string? beforeOrder)
        => AddParagraph(db, chapterId, text, null, beforeOrder);

    // Inserts a narration paragraph ordered after `afterOrder` (used for multi-line title blocks).
    public static Paragraph AddTitleParagraphAfter(
        ProjectDbContext db, Guid chapterId, string text, string afterOrder)
        => AddParagraph(db, chapterId, text, afterOrder, null);

    private static Paragraph AddParagraph(
        ProjectDbContext db, Guid chapterId, string text, string? afterOrder, string? beforeOrder)
    {
        var para = new Paragraph
        {
            Id = Guid.NewGuid(),
            ChapterId = chapterId,
            Order = OrderHelper.GetBetween(afterOrder, beforeOrder),
        };
        db.Paragraphs.Add(para);
        db.ParagraphItems.Add(new ParagraphItem
        {
            Id = Guid.NewGuid(),
            ParagraphId = para.Id,
            ItemType = ParagraphItemType.Speech,
            // Narration means the speaker is the narrator; an inserted title is no exception.
            CharacterId = ProjectDbContext.NarratorId,
            Text = text,
            Order = OrderHelper.GetBetween(null, null),
        });
        return para;
    }
}
