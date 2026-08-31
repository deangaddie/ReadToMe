using Read2Me.Data.Enums;
using Read2Me.Services.Books;

namespace Read2Me.Services
{
    internal record AttributedSegment(string Text, ParagraphItemType ItemType, Guid? CharacterId);

    /// <summary>
    /// Records the splitter's narration/dialog decision where it now lives: the speaker (ADR-0006).
    /// A narration segment is stamped with the narrator — the splitter deciding a segment is
    /// narration <em>is</em> its attribution — and a dialog segment is left unattributed for the
    /// attribution queue to answer. Every segment is a speech item; only the speaker differs.
    /// </summary>
    internal static class NarrationClassifier
    {
        internal static List<AttributedSegment> Classify(IReadOnlyList<ParagraphSegment> segments, Guid narratorId) =>
            segments
                .Select(s => new AttributedSegment(
                    s.Text,
                    ParagraphItemType.Speech,
                    s.Type == SegmentType.Narration ? narratorId : null))
                .ToList();
    }
}
