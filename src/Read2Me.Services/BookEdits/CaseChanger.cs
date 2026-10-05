using System.Text.RegularExpressions;

namespace Read2Me.Services.BookEdits
{
    /// <summary>Sentence or title case for one value. Keeps the book's names from the
    /// <see cref="ProtectionSet"/> (longest phrase first, then words) and the fixed rules: roman
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

        public static string Recase(string value, CaseMode mode, ProtectionSet protection)
        {
            var words = Tokenise(value);
            var output = value.ToCharArray();
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                var initial = IsSentenceStart(value, word.Index);
                var isolated = (i == 0 || !IsAllCaps(words[i - 1].Value))
                    && (i == words.Count - 1 || !IsAllCaps(words[i + 1].Value));
                if (!initial && isolated && IsShortAcronym(word.Value))
                    continue;

                var phrase = protection.MatchPhrase(value, words, i);
                if (phrase != null)
                {
                    for (var k = 0; k < phrase.Length; k++)
                    {
                        var stem = k == 0 && initial ? Capitalise(phrase[k]) : phrase[k];
                        var phraseWord = words[i + k];
                        (stem + phraseWord.Value[stem.Length..].ToLowerInvariant())
                            .CopyTo(0, output, phraseWord.Index, phraseWord.Length);
                    }
                    i += phrase.Length - 1;
                    continue;
                }
                RecaseWord(word.Value, initial, mode == CaseMode.Title, protection)
                    .CopyTo(0, output, word.Index, word.Length);
            }
            return new string(output);
        }

        /// <summary>The words of a value, dotted acronyms left out (they are never re-cased).</summary>
        internal static IReadOnlyList<Match> Tokenise(string value)
        {
            var acronyms = DottedAcronym.Matches(value);
            return Word.Matches(value)
                .Where(w => !acronyms.Any(a => w.Index < a.Index + a.Length && a.Index < w.Index + w.Length))
                .ToList();
        }

        /// <summary>Hyphen parts are cased one by one: each is a new word in title case only.
        /// Letters after an apostrophe stay lower. A protected word takes its restore form, which
        /// beats the small-word rule.</summary>
        private static string RecaseWord(string word, bool initial, bool title, ProtectionSet protection)
        {
            var parts = word.Split('-');
            for (var p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                var stem = Stem(part);
                var tail = part[stem.Length..].ToLowerInvariant();
                var first = p == 0 && initial;

                if (RomanNumeral.IsMatch(stem))
                {
                    // keep the numeral (or pronoun I) upper
                }
                else if (protection.Words.TryGetValue(stem.ToLowerInvariant(), out var restore)
                    && restore.Length == stem.Length)
                {
                    stem = first ? Capitalise(restore) : restore;
                }
                else
                {
                    var capitalise = first || (title && !SmallWords.Contains(part.ToLowerInvariant()));
                    stem = capitalise ? Capitalise(stem.ToLowerInvariant()) : stem.ToLowerInvariant();
                }
                parts[p] = stem + tail;
            }
            return string.Join('-', parts);
        }

        /// <summary>The value's start (after leading whitespace / opening quotes), directly after an
        /// opening quote, or after . ! ? : with optional closing quotes and whitespace. The em dash
        /// is not a boundary.</summary>
        internal static bool IsSentenceStart(string value, int start)
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

        /// <summary>The letters before an apostrophe (Seldon's → Seldon), or the whole word.</summary>
        internal static string Stem(string word)
        {
            var apostrophe = word.IndexOfAny(['\'', '’']);
            return apostrophe < 0 ? word : word[..apostrophe];
        }

        internal static bool IsAllCaps(string word)
            => word.Any(char.IsLetter) && !word.Any(char.IsLower);

        private static string Capitalise(string word) => char.ToUpperInvariant(word[0]) + word[1..];

        private static bool IsOpeningQuote(char c) => c is '“' or '"' or '‘';

        private static bool IsShortAcronym(string word)
            => word.Length is 2 or 3 && word.All(char.IsUpper);
    }
}
