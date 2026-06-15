using Read2Me.Data.Enums;
using Read2Me.Services.Books;

namespace Read2Me.Services
{
    internal record AttributedSegment(string Text, ParagraphItemType ItemType, Guid? CharacterId);

    internal static class NarrationClassifier
    {
        internal static List<AttributedSegment> Classify(IReadOnlyList<ParagraphSegment> segments, Guid narratorId) =>
            segments
                .Select(s => s.Type == SegmentType.Narration
                    ? new AttributedSegment(s.Text, ParagraphItemType.Narration, narratorId)
                    : new AttributedSegment(s.Text, ParagraphItemType.Character, null))
                .ToList();
    }
}
