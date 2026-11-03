namespace Read2Me.Services.Series
{
    /// <summary>Why a shared Voice can't be deleted (#205 item 9).</summary>
    public enum SeriesVoiceDeleteReason
    {
        /// <summary>Another linked book's positional Voice Rules use it: remove those first.</summary>
        PositionalRules,

        /// <summary>It is the series character's last Voice, so the other linked books would lose their default.</summary>
        LastVoice,
    }

    /// <summary>A book that blocks the delete, and how many of its positional rules use the Voice.</summary>
    public sealed record SeriesVoiceDeleteBook(string Folder, string Title, int RuleCount);

    public sealed record SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason Reason, IReadOnlyList<SeriesVoiceDeleteBook> Books);

    /// <summary>
    /// When deleting a shared Voice is refused (#205 item 9, #213 Refusals). Only the other linked
    /// books count: the book the delete is made in drops its own rules with the Voice, as a
    /// single-book delete always has, and each book's default rule moves to its oldest remaining
    /// Voice.
    /// </summary>
    public static class SeriesVoiceDeleteRules
    {
        /// <param name="isLastVoice">The Voice is the series character's only one.</param>
        /// <param name="others">The other linked books in Series position order, with their positional rules that use the Voice.</param>
        public static SeriesVoiceDeleteBlock? Check(bool isLastVoice, IReadOnlyList<SeriesVoiceDeleteBook> others)
        {
            var ruled = others.Where(b => b.RuleCount > 0).ToList();
            if (ruled.Count > 0) return new SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason.PositionalRules, ruled);
            return isLastVoice && others.Count > 0
                ? new SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason.LastVoice, others)
                : null;
        }

        /// <summary>The refusal's detail, naming the books.</summary>
        public static string Describe(string voiceName, SeriesVoiceDeleteBlock block)
        {
            var titles = string.Join(", ", block.Books.Select(b => $"'{b.Title}'"));
            return block.Reason == SeriesVoiceDeleteReason.PositionalRules
                ? $"\"{voiceName}\" is a series Voice that Voice Rules use in {titles}. Remove those rules first."
                : $"\"{voiceName}\" is the series character's last Voice: {titles} would have no default Voice. Add another Voice first.";
        }
    }
}
