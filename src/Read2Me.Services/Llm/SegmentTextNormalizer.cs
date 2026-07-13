using System.Text;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Canonical text form for comparing LLM segment text against book text: whitespace runs
    /// collapse to a single space, curly quotes fold to straight, and the dash class
    /// (em-dash, en-dash, hyphen runs) folds to a single hyphen. Letters, digits and all other
    /// punctuation are preserved exactly — normalization only absorbs the differences the models
    /// were observed to introduce (ticket 05), never real content.
    /// </summary>
    internal static class SegmentTextNormalizer
    {
        public static string Normalize(string text) => Build(text, out _);

        /// <summary>
        /// Normalizes and returns, via <paramref name="map"/>, the index into <paramref name="text"/>
        /// of the original character (first of its run for collapsed runs) behind each normalized
        /// character — so alignment can slice the original text at normalized match positions.
        /// </summary>
        public static string Build(string text, out int[] map)
        {
            var chars = new StringBuilder(text.Length);
            var indexes = new List<int>(text.Length);

            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    var start = i;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (chars.Length > 0 && i < text.Length)
                    {
                        chars.Append(' ');
                        indexes.Add(start);
                    }
                }
                else if (IsDash(c))
                {
                    var start = i;
                    while (i < text.Length && IsDash(text[i])) i++;
                    chars.Append('-');
                    indexes.Add(start);
                }
                else
                {
                    chars.Append(FoldQuote(c));
                    indexes.Add(i);
                    i++;
                }
            }

            map = indexes.ToArray();
            return chars.ToString();
        }

        private static bool IsDash(char c) =>
            c is '-' or '–' or '—' or '―'; // hyphen, en-dash, em-dash, horizontal bar

        private static char FoldQuote(char c) => c switch
        {
            '‘' or '’' or '‚' or 'ʼ' => '\'',
            '“' or '”' or '„' => '"',
            _ => c,
        };
    }
}
