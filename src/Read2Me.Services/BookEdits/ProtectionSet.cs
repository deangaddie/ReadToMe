using System.Text.RegularExpressions;

namespace Read2Me.Services.BookEdits
{
    /// <summary>
    /// The book's own names, learned from its capitalisation, that sentence and title case keep.
    /// Keys are lowercase; each value is the restore form — the most frequent mid-sentence spelling
    /// (so inner capitals such as MacArthur survive). A word or phrase is protected when it appears
    /// capitalised mid-sentence at least once and in at least <see cref="Threshold"/> of its
    /// mid-sentence-or-lowercase occurrences. All-caps tokens carry no case evidence, single letters
    /// never count, and sentence-initial capitals are ignored.
    /// </summary>
    public sealed class ProtectionSet
    {
        public const double Threshold = 0.3;

        /// <summary>Lowercase words allowed between the capitalised words of a phrase.</summary>
        private static readonly HashSet<string> Connectors = ["of", "the", "and", "de", "von", "van"];

        public static ProtectionSet Empty { get; } = new(new Dictionary<string, string>(), new Dictionary<string, string>());

        internal IReadOnlyDictionary<string, string> Phrases { get; }
        internal IReadOnlyDictionary<string, string> Words { get; }

        private readonly int _longestPhrase;

        private ProtectionSet(IReadOnlyDictionary<string, string> phrases, IReadOnlyDictionary<string, string> words)
        {
            Phrases = phrases;
            Words = words;
            _longestPhrase = phrases.Keys.Select(k => k.Split(' ').Length).DefaultIfEmpty(0).Max();
        }

        /// <summary>Learns the set from the book's text: every paragraph item and every title.</summary>
        public static ProtectionSet Build(IEnumerable<string> corpus)
        {
            var words = new Tally();
            var phrases = new Tally();
            var texts = new List<(string Text, IReadOnlyList<Match> Tokens)>();
            foreach (var text in corpus)
            {
                var tokens = CaseChanger.Tokenise(text);
                texts.Add((text, tokens));
                CountWords(text, tokens, words);
                CountPhraseRuns(text, tokens, phrases);
            }
            CountLowercasePhrases(texts, phrases);
            return new ProtectionSet(phrases.Protected(), words.Protected());
        }

        /// <summary>
        /// The restore form of the longest protected phrase starting at word <paramref name="i"/>,
        /// one spelling per word (the stem, before any apostrophe), or null when none starts there.
        /// </summary>
        internal string[]? MatchPhrase(string value, IReadOnlyList<Match> words, int i)
        {
            for (var length = Math.Min(_longestPhrase, words.Count - i); length >= 2; length--)
            {
                var key = new string[length];
                for (var k = 0; k < length; k++)
                    key[k] = PhraseWord(words[i + k])?.ToLowerInvariant() ?? "";
                if (Phrases.TryGetValue(string.Join(' ', key), out var restore)
                    && SpellsPhrase(value, words, i, key, w => w.ToLowerInvariant()))
                {
                    var restoreWords = restore.Split(' ');
                    if (restoreWords.Select((w, k) => w.Length == key[k].Length).All(same => same))
                        return restoreWords;
                }
            }
            return null;
        }

        /// <summary>Possessives count on the stem; hyphenated words count per part.</summary>
        private static void CountWords(string text, IReadOnlyList<Match> tokens, Tally words)
        {
            foreach (var token in tokens)
            {
                var initial = CaseChanger.IsSentenceStart(text, token.Index);
                var parts = token.Value.Split('-');
                for (var p = 0; p < parts.Length; p++)
                {
                    var stem = CaseChanger.Stem(parts[p]);
                    if (stem.Length < 2 || CaseChanger.IsAllCaps(stem))
                        continue;
                    var key = stem.ToLowerInvariant();
                    if (stem == key)
                        words.AddLowercase(key);
                    else if (!(p == 0 && initial))
                        words.AddCapitalisedMidSentence(key, stem);
                }
            }
        }

        /// <summary>Capitalised runs of two or more words, space-separated, with connectors allowed
        /// between capitalised words. A hyphenated token ends a run; a possessive closes it. When a
        /// run starts a sentence its first word is no evidence, so only the rest counts.</summary>
        private static void CountPhraseRuns(string text, IReadOnlyList<Match> tokens, Tally phrases)
        {
            var run = new List<int>();

            void Flush()
            {
                if (run.Count > 0 && CaseChanger.IsSentenceStart(text, tokens[run[0]].Index))
                    run.RemoveAt(0);
                while (run.Count > 0 && !IsCapitalised(PhraseWord(tokens[run[0]])))
                    run.RemoveAt(0);
                while (run.Count > 0 && !IsCapitalised(PhraseWord(tokens[run[^1]])))
                    run.RemoveAt(run.Count - 1);
                if (run.Count >= 2)
                {
                    var spelling = string.Join(' ', run.Select(i => PhraseWord(tokens[i])));
                    phrases.AddCapitalisedMidSentence(spelling.ToLowerInvariant(), spelling);
                }
                run.Clear();
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                if (run.Count > 0 && !SpaceBetween(text, tokens[run[^1]], tokens[i]))
                    Flush();

                var word = PhraseWord(tokens[i]);
                if (IsCapitalised(word))
                {
                    run.Add(i);
                    if (word!.Length < tokens[i].Length)
                        Flush(); // possessive: the phrase ends here
                }
                else if (run.Count > 0 && word != null && Connectors.Contains(word) && word.Length == tokens[i].Length)
                {
                    run.Add(i);
                }
                else
                {
                    Flush();
                }
            }
            Flush();
        }

        /// <summary>Lowercase occurrences of the candidate phrases, the ratio's other side.</summary>
        private static void CountLowercasePhrases(List<(string Text, IReadOnlyList<Match> Tokens)> texts, Tally phrases)
        {
            var byFirstWord = phrases.Keys
                .Select(k => k.Split(' '))
                .GroupBy(w => w[0])
                .ToDictionary(g => g.Key, g => g.ToList());
            if (byFirstWord.Count == 0)
                return;

            foreach (var (text, tokens) in texts)
            {
                for (var i = 0; i < tokens.Count; i++)
                {
                    var first = PhraseWord(tokens[i]);
                    if (first == null || !byFirstWord.TryGetValue(first, out var candidates))
                        continue;
                    foreach (var candidate in candidates)
                    {
                        if (SpellsPhrase(text, tokens, i, candidate, w => w))
                            phrases.AddLowercase(string.Join(' ', candidate));
                    }
                }
            }
        }

        /// <summary>Whether tokens i… spell <paramref name="phrase"/> word for word (after
        /// <paramref name="normalise"/>), space-separated, with only the last allowed a possessive.</summary>
        private static bool SpellsPhrase(
            string text, IReadOnlyList<Match> tokens, int i, IReadOnlyList<string> phrase, Func<string, string> normalise)
        {
            if (i + phrase.Count > tokens.Count)
                return false;
            for (var k = 0; k < phrase.Count; k++)
            {
                var token = tokens[i + k];
                var word = PhraseWord(token);
                if (word == null || normalise(word) != phrase[k])
                    return false;
                if (k < phrase.Count - 1 && word.Length < token.Length)
                    return false;
                if (k > 0 && !SpaceBetween(text, tokens[i + k - 1], token))
                    return false;
            }
            return true;
        }

        /// <summary>A token's word in a phrase: its stem before an apostrophe, or null when it is
        /// hyphenated (phrases never span a hyphen).</summary>
        private static string? PhraseWord(Match token)
            => token.Value.Contains('-') ? null : CaseChanger.Stem(token.Value);

        private static bool IsCapitalised(string? word)
            => word != null && char.IsUpper(word[0]) && !CaseChanger.IsAllCaps(word);

        private static bool SpaceBetween(string text, Match left, Match right)
        {
            var start = left.Index + left.Length;
            return right.Index > start && text.AsSpan(start, right.Index - start).IsWhiteSpace();
        }

        /// <summary>Capitalised-mid-sentence and lowercase counts per key, with the spellings seen.</summary>
        private sealed class Tally
        {
            private sealed class Entry
            {
                public int CapitalisedMidSentence;
                public int Lowercase;
                public readonly Dictionary<string, int> Spellings = new(StringComparer.Ordinal);
            }

            private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

            public IEnumerable<string> Keys => _entries.Keys;

            public void AddCapitalisedMidSentence(string key, string spelling)
            {
                var entry = For(key);
                entry.CapitalisedMidSentence++;
                entry.Spellings[spelling] = entry.Spellings.GetValueOrDefault(spelling) + 1;
            }

            public void AddLowercase(string key) => For(key).Lowercase++;

            public IReadOnlyDictionary<string, string> Protected()
                => _entries
                    .Where(e => e.Value.CapitalisedMidSentence >= 1
                        && (double)e.Value.CapitalisedMidSentence
                            / (e.Value.CapitalisedMidSentence + e.Value.Lowercase) >= Threshold)
                    .ToDictionary(
                        e => e.Key,
                        // Ties break by spelling, so the book's text order never changes the result.
                        e => e.Value.Spellings
                            .OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal)
                            .First().Key,
                        StringComparer.Ordinal);

            private Entry For(string key)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    _entries[key] = entry = new Entry();
                return entry;
            }
        }
    }
}
