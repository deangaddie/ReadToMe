namespace Read2Me.Services.Voice
{
    /// <summary>
    /// Absolute position of a ParagraphItem in the story: the 5-tuple of sibling-scoped
    /// Order keys up its ancestry, compared lexicographically (Volume first, ties break downward).
    /// </summary>
    public readonly record struct StoryPosition(
        string Volume,
        string Part,
        string Chapter,
        string Paragraph,
        string Item) : IComparable<StoryPosition>
    {
        public int CompareTo(StoryPosition other)
        {
            var c = string.CompareOrdinal(Volume, other.Volume);
            if (c != 0) return c;
            c = string.CompareOrdinal(Part, other.Part);
            if (c != 0) return c;
            c = string.CompareOrdinal(Chapter, other.Chapter);
            if (c != 0) return c;
            c = string.CompareOrdinal(Paragraph, other.Paragraph);
            if (c != 0) return c;
            return string.CompareOrdinal(Item, other.Item);
        }
    }
}
