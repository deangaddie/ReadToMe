using static Read2Me.Services.Characters.Rules.SpeechLexicon;

namespace Read2Me.Services.Characters.Rules
{
    /// <summary>One roster character as the rules see it: its name and aliases (the seed Narrator row is never one).</summary>
    internal sealed record RosterEntry(string Name, IReadOnlyList<string> Aliases);

    /// <summary>How a mention matched the roster.</summary>
    internal enum NameMatchStatus { Roster, Ambiguous, Unknown }

    /// <summary>A mention's match: <see cref="Name"/> is the roster entry's name when <see cref="Status"/> is <see cref="NameMatchStatus.Roster"/>.</summary>
    internal readonly record struct NameMatch(NameMatchStatus Status, string? Name = null)
    {
        public static readonly NameMatch Unknown = new(NameMatchStatus.Unknown);
        public static readonly NameMatch Ambiguous = new(NameMatchStatus.Ambiguous);

        /// <summary>The prototype's status word (<c>roster</c>, <c>ambiguous</c>, <c>unknown</c>), as unresolved reasons carry it.</summary>
        public string StatusWord => Status switch
        {
            NameMatchStatus.Roster => "roster",
            NameMatchStatus.Ambiguous => "ambiguous",
            _ => "unknown",
        };
    }

    /// <summary>
    /// Roster name matching (the prototype's <c>buildNameIndex</c>). A capitalised name or alias becomes a form:
    /// its honorific words and its other words. A mention matches a form when all its non-honorific
    /// words are the form's, and its honorifics (if both have any) are too; exactly one matching
    /// character is a hit, several are ambiguous. An all-lowercase alias ("the magician") is a noun
    /// phrase looked up whole, without its article.
    /// </summary>
    internal sealed class NameIndex
    {
        private sealed record Form(string Entry, HashSet<string> Hon, HashSet<string> Rest);

        private readonly List<Form> _forms = [];

        /// <summary>Noun alias → character name, or null when two characters share it.</summary>
        private readonly Dictionary<string, string?> _nounAliases = new(StringComparer.Ordinal);

        private NameIndex(IReadOnlyList<RosterEntry> roster)
        {
            foreach (var entry in roster)
            {
                foreach (var form in entry.Aliases.Prepend(entry.Name))
                {
                    if (!form.Any(c => c is >= 'A' and <= 'Z'))
                    {
                        var key = JsTrim(StripArticle(Lc(form)));
                        _nounAliases[key] = _nounAliases.TryGetValue(key, out var had) && had != entry.Name ? null : entry.Name;
                        continue;
                    }
                    var tokens = Words(form).Select(Strip).ToList();
                    _forms.Add(new Form(
                        entry.Name,
                        [.. tokens.Where(Honorifics.Contains)],
                        [.. tokens.Where(t => !Honorifics.Contains(t))]));
                }
            }
            CapWords = _forms.SelectMany(f => f.Rest).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>Every non-honorific word of a capitalised name or alias, stripped.</summary>
        public IReadOnlySet<string> CapWords { get; }

        public static NameIndex Build(IReadOnlyList<RosterEntry> roster) => new(roster);

        /// <summary>Matches capitalised mention tokens ("Lord", "Borric") against the roster forms.</summary>
        public NameMatch Match(IReadOnlyList<string> tokens)
        {
            var stripped = tokens.Select(Strip).ToList();
            var hon = stripped.Where(Honorifics.Contains).ToList();
            var rest = stripped.Where(t => !Honorifics.Contains(t)).ToList();
            if (rest.Count == 0)
                return NameMatch.Unknown;

            var hits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var form in _forms)
            {
                if (!rest.All(form.Rest.Contains))
                    continue;
                if (hon.Count > 0 && form.Hon.Count > 0 && !hon.All(form.Hon.Contains))
                    continue;
                hits.Add(form.Entry);
            }
            return hits.Count switch
            {
                1 => new NameMatch(NameMatchStatus.Roster, hits.First()),
                > 1 => NameMatch.Ambiguous,
                _ => NameMatch.Unknown,
            };
        }

        /// <summary>Looks a lowercase noun phrase ("magician", "old man") up among the noun aliases.</summary>
        public NameMatch Noun(IReadOnlyList<string> words)
        {
            var key = string.Join(" ", words.Select(Lc));
            if (!_nounAliases.TryGetValue(key, out var name))
                return NameMatch.Unknown;
            if (name is null)
                return NameMatch.Ambiguous;
            return name.Length > 0 ? new NameMatch(NameMatchStatus.Roster, name) : NameMatch.Unknown;
        }

        /// <summary><c>replace(/^(the|a|an)\s+/, '')</c>.</summary>
        private static string StripArticle(string s)
        {
            foreach (var article in (string[])["the", "a", "an"])
            {
                if (s.Length > article.Length && s.StartsWith(article, StringComparison.Ordinal) && IsJsSpace(s[article.Length]))
                {
                    var i = article.Length;
                    while (i < s.Length && IsJsSpace(s[i]))
                        i++;
                    return s[i..];
                }
            }
            return s;
        }
    }
}
