using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Pure sentence splitter for sentence-chunked TTS. Splits text on sentence terminators
    /// (<c>. ! ?</c>) followed by whitespace, guarding against abbreviations, decimals, and
    /// ellipsis. Short fragments are handled by <see cref="SentenceChunker"/>'s greedy packing.
    /// </summary>
    public static class SentenceSplitter
    {
        // Known titles/abbreviations whose trailing period must not be treated as a sentence end.
        private static readonly string[] Abbreviations =
        {
            "Mr", "Mrs", "Ms", "Dr", "Prof", "St", "Sr", "Jr",
            "vs", "etc", "e.g", "i.e", "Inc", "Ltd", "Co",
        };

        // A terminator (. ! ?) followed by whitespace marks a candidate split point.
        private static readonly Regex SplitPoints = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

        // A period between two digits is a decimal point, not a sentence end (e.g. "3.14").
        private static readonly Regex Decimal = new(@"(?<=\d)\.(?=\d)", RegexOptions.Compiled);

        // Two or more consecutive dots are an ellipsis, not a sentence end.
        private static readonly Regex Ellipsis = new(@"\.{2,}", RegexOptions.Compiled);

        // Stand-in for a period that must not trigger a split; a control char unlikely in prose.
        private const char DotPlaceholder = '';

        public static IReadOnlyList<string> Split(string text)
        {
            var guarded = Guard(text);

            var pieces = SplitPoints.Split(guarded);

            var restored = new List<string>(pieces.Length);
            foreach (var piece in pieces)
                restored.Add(Unguard(piece));

            return restored;
        }

        /// <summary>Masks periods that must not trigger a split (abbreviations, decimals, ellipsis).</summary>
        private static string Guard(string text)
        {
            var guarded = Ellipsis.Replace(text, m => new string(DotPlaceholder, m.Length));
            guarded = Decimal.Replace(guarded, DotPlaceholder.ToString());

            foreach (var abbr in Abbreviations)
                guarded = Regex.Replace(guarded, Regex.Escape(abbr) + @"\.", abbr + DotPlaceholder);

            return guarded;
        }

        private static string Unguard(string text) => text.Replace(DotPlaceholder, '.');
    }
}
