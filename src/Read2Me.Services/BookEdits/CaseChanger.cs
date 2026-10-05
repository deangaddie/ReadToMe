using System.Text.RegularExpressions;

namespace Read2Me.Services.BookEdits
{
    /// <summary>Sentence or title case for one value, with the fixed rules that keep roman
    /// numerals (and the pronoun I), short isolated acronyms and dotted acronyms. Re-cases the
    /// whole value so sentence starts and neighbouring words are read in full context; the
    /// result has the same length as the input, so callers can take matched spans from it.</summary>
    internal static class CaseChanger
    {
        // Letters joined by apostrophes or hyphens; a word touching a digit (PS3551) is not a word.
        private static readonly Regex Word = new(
            @"(?<![\p{L}\p{M}\p{N}])[\p{L}\p{M}]+(?:['’-][\p{L}\p{M}]+)*(?![\p{L}\p{M}\p{N}])",
            RegexOptions.Compiled);

        private static readonly Regex DottedAcronym = new(@"(?<![\p{L}\p{M}])(?:[A-Za-z]\.){2,}", RegexOptions.Compiled);

        // I/V/X only: L, C, D and M are left out so MIX and DIV stay words.
        private static readonly Regex RomanNumeral = new(@"^(?=[IVX])X{0,3}(?:IX|IV|V?I{0,3})$", RegexOptions.Compiled);

        private static readonly HashSet<string> SmallWords =
            ["a", "an", "the", "and", "but", "or", "nor", "for", "of", "to", "in", "on", "at", "by", "as"];

        public static string Recase(string value, CaseMode mode)
        {
            var acronyms = DottedAcronym.Matches(value);
            var words = Word.Matches(value)
                .Where(w => !acronyms.Any(a => w.Index < a.Index + a.Length && a.Index < w.Index + w.Length))
                .ToList();

            var output = value.ToCharArray();
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                var initial = IsSentenceStart(value, word.Index);
                var isolated = (i == 0 || !IsAllCaps(words[i - 1].Value))
                    && (i == words.Count - 1 || !IsAllCaps(words[i + 1].Value));
                if (!initial && isolated && IsShortAcronym(word.Value))
                    continue;
                RecaseWord(word.Value, initial, mode == CaseMode.Title).CopyTo(0, output, word.Index, word.Length);
            }
            return new string(output);
        }

        /// <summary>Hyphen parts are cased one by one: each is a new word in title case only.
        /// Letters after an apostrophe stay lower.</summary>
        private static string RecaseWord(string word, bool initial, bool title)
        {
            var parts = word.Split('-');
            for (var p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                var apostrophe = part.IndexOfAny(['\'', '’']);
                var stem = apostrophe < 0 ? part : part[..apostrophe];
                var tail = apostrophe < 0 ? "" : part[apostrophe..].ToLowerInvariant();

                var capitalise = (p == 0 && initial)
                    || (title && !SmallWords.Contains(part.ToLowerInvariant()));
                if (!RomanNumeral.IsMatch(stem))
                {
                    stem = capitalise
                        ? char.ToUpperInvariant(stem[0]) + stem[1..].ToLowerInvariant()
                        : stem.ToLowerInvariant();
                }
                parts[p] = stem + tail;
            }
            return string.Join('-', parts);
        }

        /// <summary>The value's start (after leading whitespace / opening quotes), directly after an
        /// opening quote, or after . ! ? : with optional closing quotes and whitespace. The em dash
        /// is not a boundary.</summary>
        private static bool IsSentenceStart(string value, int start)
        {
            if (start == 0 || IsOpeningQuote(value[start - 1]))
                return true;
            if (value.Take(start).All(c => char.IsWhiteSpace(c) || IsOpeningQuote(c)))
                return true;

            var i = start - 1;
            if (!char.IsWhiteSpace(value[i]))
                return false;
            while (i >= 0 && char.IsWhiteSpace(value[i]))
                i--;
            while (i >= 0 && value[i] is '”' or '"' or '’' or '\'')
                i--;
            return i >= 0 && value[i] is '.' or '!' or '?' or ':';
        }

        private static bool IsOpeningQuote(char c) => c is '“' or '"' or '‘';

        private static bool IsAllCaps(string word)
            => word.Any(char.IsLetter) && !word.Any(char.IsLower);

        private static bool IsShortAcronym(string word)
            => word.Length is 2 or 3 && word.All(char.IsUpper);
    }
}
