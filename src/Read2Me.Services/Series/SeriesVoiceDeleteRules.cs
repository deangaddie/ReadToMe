namespace Read2Me.Services.Series
{
    /// <summary>Why a shared Voice can't be deleted (#205 item 9).</summary>
    public enum SeriesVoiceDeleteReason
    {
        /// <summary>Another linked book's positional Voice Rules use it: remove those first.</summary>
        PositionalRules,

        /// <summary>It is another linked book's only Voice, so that book would lose its default.</summary>
        LastVoice,
    }

    /// <summary>A book that blocks the delete, and how many of its positional rules use the Voice.</summary>
    public sealed record SeriesVoiceDeleteBook(string Folder, string Title, int RuleCount);

    public sealed record SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason Reason, IReadOnlyList<SeriesVoiceDeleteBook> Books);

    /// <summary>
    /// When deleting a shared Voice is refused (#205 item 9, #213 Refusals). Only the other linked
    /// books count: the book the delete is made in drops its own rules with the Voice, as a
    /// single-book delete always has, and each book's default rule moves to its oldest remaining
    /// Voice. A book that differs from series may not hold the Voice at all, or hold others besides
    /// it (#222), so each book is judged by what it holds.
    /// </summary>
    public static class SeriesVoiceDeleteRules
    {
        /// <summary>
        /// Another linked book: its positional rules that use the Voice, whether it holds the Voice,
        /// and whether it holds any other Voice of its linked Character.
        /// </summary>
        public sealed record OtherBook(string Folder, string Title, int RuleCount, bool HoldsVoice, bool HoldsOtherVoices);

        /// <param name="others">The other linked books in Series position order.</param>
        public static SeriesVoiceDeleteBlock? Check(IReadOnlyList<OtherBook> others)
        {
            var ruled = others.Where(b => b.RuleCount > 0).Select(Named).ToList();
            if (ruled.Count > 0) return new SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason.PositionalRules, ruled);
            var defaultless = others.Where(b => b.HoldsVoice && !b.HoldsOtherVoices).Select(Named).ToList();
            return defaultless.Count > 0 ? new SeriesVoiceDeleteBlock(SeriesVoiceDeleteReason.LastVoice, defaultless) : null;
        }

        /// <summary>The refusal's detail, naming the books.</summary>
        public static string Describe(string voiceName, SeriesVoiceDeleteBlock block)
        {
            var titles = string.Join(", ", block.Books.Select(b => $"'{b.Title}'"));
            return block.Reason == SeriesVoiceDeleteReason.PositionalRules
                ? $"\"{voiceName}\" is a series Voice that Voice Rules use in {titles}. Remove those rules first."
                : $"\"{voiceName}\" is the last Voice of {titles}, which would have no default Voice. Add another Voice first.";
        }

        private static SeriesVoiceDeleteBook Named(OtherBook b) => new(b.Folder, b.Title, b.RuleCount);
    }
}
