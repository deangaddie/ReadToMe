using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Read2Me.Services.Characters.Rules
{
    /// <summary>
    /// The word lists and text helpers of the speech-tag rules, copied verbatim from the JavaScript
    /// prototype they were measured with. JavaScript semantics are kept where they could differ: letter tests
    /// are ASCII (<c>/[A-Z]/</c>), whitespace is ECMAScript <c>\s</c>, and <c>$</c> is end of string.
    /// </summary>
    internal static partial class SpeechLexicon
    {
        public static readonly FrozenSet<string> Honorifics = FrozenSet.ToFrozenSet(
        [
            "mr", "mrs", "ms", "miss", "sir", "lady", "lord", "master", "prince", "princess", "duke", "duchess",
            "king", "queen", "father", "mother", "brother", "sister", "dr", "doctor", "captain", "colonel", "uncle", "aunt", "madame",
            "madam", "monsieur", "count", "countess", "baron", "baroness", "earl", "squire", "professor", "mistress", "dame", "general",
            "lieutenant", "sergeant", "major", "inspector", "reverend", "commander", "admiral", "baronet", "emperor", "empress",
        ], StringComparer.Ordinal);

        public static readonly FrozenSet<string> SpeechVerbs = FrozenSet.ToFrozenSet(
            """
            said says say asked asks replied replies answered answers cried cries shouted shouts yelled yells
            called whispered whispers muttered mutters murmured murmurs exclaimed exclaims added adds continued continues began begins
            repeated repeats returned rejoined observed observes remarked remarks responded responds retorted retorts countered counters
            injected interjected interrupted interrupts insisted insists demanded demands commanded ordered snapped snaps growled growls
            laughed chuckled sighed gasped screamed screams roared roars bellowed hissed grunted snorted agreed agrees admitted conceded
            explained explains suggested suggests offered protested objected declared declares announced announces inquired enquired
            inquires enquires queried pleaded begged urged warned mumbled stammered stuttered breathed finished concluded corrected teased
            joked quipped wailed sobbed spoke noted mused ventured prompted persisted pressed sneered scoffed grumbled complained
            shrieked howled barked thundered purred drawled intoned chimed piped sang croaked gushed managed blurted
            """.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        public static readonly FrozenSet<string> Pron = FrozenSet.ToFrozenSet(["he", "she", "they", "i", "we"], StringComparer.Ordinal);

        public static readonly FrozenSet<string> Det = FrozenSet.ToFrozenSet(
        [
            "the", "a", "an", "his", "her", "their", "its", "this", "that", "one", "another", "my", "our", "your", "some", "every", "each",
        ], StringComparer.Ordinal);

        public static readonly FrozenSet<string> Prep = FrozenSet.ToFrozenSet(
        [
            "to", "at", "with", "from", "for", "of", "by", "toward", "towards", "upon", "on", "into", "onto", "about",
            "behind", "beside", "before", "after", "over", "under", "near", "than", "like", "as", "in", "through", "across", "against", "around", "past",
        ], StringComparer.Ordinal);

        public static readonly FrozenSet<string> NpStop = FrozenSet.ToFrozenSet(
        [
            "who", "which", "that", "and", "but", "or", "then", "was", "were", "had", "has", "is", "did", "could", "would", "should", "might", "must", "will",
        ], StringComparer.Ordinal);

        /// <summary>Sentence-initial words that introduce a leading adverbial clause: the subject comes after the first comma.</summary>
        public static readonly FrozenSet<string> LeadClause = FrozenSet.ToFrozenSet(
        [
            "as", "when", "while", "with", "after", "before", "without", "since", "though", "although", "if", "once",
            "at", "in", "on", "for", "from", "during", "until", "because", "despite", "whilst", "upon", "by", "under", "over", "having",
        ], StringComparer.Ordinal);

        /// <summary>Single-word openers the subject directly follows.</summary>
        public static readonly FrozenSet<string> LeadWord = FrozenSet.ToFrozenSet(
        [
            "then", "but", "and", "so", "yet", "still", "now", "finally", "suddenly", "abruptly", "again", "just", "here", "there",
        ], StringComparer.Ordinal);

        public static readonly FrozenSet<string> AdverbLike = FrozenSet.ToFrozenSet(
        [
            "then", "also", "again", "finally", "simply", "only", "just", "still", "once", "quietly", "back", "up", "down", "out", "aloud", "nearly", "almost", "at", "last",
        ], StringComparer.Ordinal);

        public static readonly FrozenSet<string> Masc = FrozenSet.ToFrozenSet(["he", "him", "his", "himself"], StringComparer.Ordinal);

        public static readonly FrozenSet<string> Fem = FrozenSet.ToFrozenSet(["she", "her", "hers", "herself"], StringComparer.Ordinal);

        public static readonly FrozenSet<string> Reported = FrozenSet.ToFrozenSet(
        [
            "he", "she", "they", "i", "we", "that", "nothing", "so", "it", "yes", "no", "something", "little", "how", "what", "whether", "if",
        ], StringComparer.Ordinal);

        /// <summary>ECMAScript <c>\s</c>, which differs from .NET's (NEL, BOM).</summary>
        private const string JsSpace = "[\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff]";

        public static string Lc(string s) => s.ToLowerInvariant();

        /// <summary>Lower case, periods removed, a trailing possessive <c>'s</c> dropped (the prototype's <c>strip</c>).</summary>
        public static string Strip(string s) => DropPossessive(Lc(s).Replace(".", "", StringComparison.Ordinal));

        /// <summary>A trailing <c>’s</c> or <c>'s</c> removed.</summary>
        public static string DropPossessive(string s) =>
            s.Length >= 2 && s[^1] == 's' && s[^2] is '’' or '\'' ? s[..^2] : s;

        public static bool IsCap(string? t) => t is { Length: > 0 } && t[0] is >= 'A' and <= 'Z';

        public static bool IsLowerStart(string? t) => t is { Length: > 0 } && t[0] is >= 'a' and <= 'z';

        public static bool IsPoss(string? t) =>
            t is { Length: >= 2 }
            && ((t[^1] == 's' && t[^2] is '’' or '\'') || (t[^1] is '’' or '\'' && t[^2] == 's'));

        public static bool IsAdverb(string t) => LyAdverb().IsMatch(t) || AdverbLike.Contains(t);

        public static bool IsPunct(string? t) => t is { Length: 1 } && t[0] is ',' or ';' or ':' or '—' or '–';

        public static bool IsJsSpace(char c) => c switch
        {
            '\t' or '\n' or '\v' or '\f' or '\r' or ' ' => true,
            _ => (int)c is 0x00a0 or 0x1680 or (>= 0x2000 and <= 0x200a) or 0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000 or 0xfeff,
        };

        /// <summary>ECMAScript <c>String.prototype.trim</c>.</summary>
        public static string JsTrim(string s)
        {
            var start = 0;
            var end = s.Length;
            while (start < end && IsJsSpace(s[start]))
                start++;
            while (end > start && IsJsSpace(s[end - 1]))
                end--;
            return s[start..end];
        }

        /// <summary><c>replace(/[\s“"]+$/, '')</c>: trailing whitespace and opening quotes removed.</summary>
        public static string TrimEndOpenQuotes(string s)
        {
            var end = s.Length;
            while (end > 0 && (IsJsSpace(s[end - 1]) || s[end - 1] is '“' or '"'))
                end--;
            return s[..end];
        }

        /// <summary><c>replace(/^[\s,]+/, '')</c>: leading whitespace and commas removed.</summary>
        public static string TrimStartCommas(string s)
        {
            var start = 0;
            while (start < s.Length && (IsJsSpace(s[start]) || s[start] == ','))
                start++;
            return s[start..];
        }

        /// <summary><c>split(/\s+/)</c> with empty parts dropped.</summary>
        public static string[] Words(string s) => JsSpaces().Split(s).Where(w => w.Length > 0).ToArray();

        /// <summary>The prototype's tokenizer: abbreviated honorifics, words (with apostrophes and hyphens), and clause punctuation.</summary>
        public static List<string> Tokenize(string s) => [.. Token().Matches(s).Select(m => m.Value)];

        /// <summary>Sentences, split after <c>.!?</c> and rejoined after an abbreviated honorific; only those with a letter.</summary>
        public static List<string> SplitSentences(string text)
        {
            var output = new List<string>();
            foreach (var part in SentenceBreak().Split(text))
            {
                if (output.Count > 0 && HonorificAbbreviationAtEnd().IsMatch(output[^1]))
                    output[^1] += " " + part;
                else
                    output.Add(part);
            }
            return [.. output.Select(JsTrim).Where(s => AsciiLetter().IsMatch(s))];
        }

        [GeneratedRegex("^[a-z]+ly\\z", RegexOptions.CultureInvariant)]
        private static partial Regex LyAdverb();

        [GeneratedRegex("(?:Mr|Mrs|Ms|Dr|St)\\.|[A-Za-z][A-Za-z’'\\-]*|[,;:—–]", RegexOptions.CultureInvariant)]
        private static partial Regex Token();

        [GeneratedRegex("(?<=[.!?])" + JsSpace + "+", RegexOptions.CultureInvariant)]
        private static partial Regex SentenceBreak();

        [GeneratedRegex(JsSpace + "+", RegexOptions.CultureInvariant)]
        private static partial Regex JsSpaces();

        /// <summary><c>/\b(Mr|Mrs|Ms|Dr|St)\.$/</c> with JavaScript's ASCII word boundary.</summary>
        [GeneratedRegex("(?<![A-Za-z0-9_])(?:Mr|Mrs|Ms|Dr|St)\\.\\z", RegexOptions.CultureInvariant)]
        private static partial Regex HonorificAbbreviationAtEnd();

        [GeneratedRegex("[A-Za-z]", RegexOptions.CultureInvariant)]
        private static partial Regex AsciiLetter();
    }
}
