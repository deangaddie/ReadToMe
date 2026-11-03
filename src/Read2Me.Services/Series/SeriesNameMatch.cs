using System.Text.RegularExpressions;
using Read2Me.Services.Characters;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Link suggestion matching (#212, context/series.md "Link suggestion"), pure: no database. A
    /// Character's <i>forms</i> are its name and aliases, each case-folded, its punctuation dropped
    /// (word-internal apostrophes and hyphens kept) and its honorific words set aside. An
    /// all-lowercase noun alias ("the doctor") and an honorific-only form ("Doctor") never take part.
    /// Two forms match when their other words are the same set and, where both carry honorifics,
    /// those agree: "Sherlock Holmes" is "Holmes, Sherlock", but "John" is not "John Watson" and
    /// "Mr. Hudson" is not "Mrs. Hudson". The seed Narrator row matches only the seed Narrator row,
    /// by its flag. Suggestions are one-to-one only. Unlike <c>NameIndex</c>, whose containment rule
    /// suits mentions in text, this compares identities.
    /// </summary>
    public static partial class SeriesNameMatch
    {
        /// <summary>A form: its honorific words and its other words, lowercase.</summary>
        public sealed record Form(IReadOnlySet<string> Honorifics, IReadOnlySet<string> Words)
        {
            public bool Matches(Form other) =>
                Words.SetEquals(other.Words)
                && (Honorifics.Count == 0 || other.Honorifics.Count == 0 || Honorifics.SetEquals(other.Honorifics));
        }

        /// <summary>A name or alias as a form, or null when it takes no part (all lowercase, or honorifics only).</summary>
        public static Form? FormOf(string text)
        {
            if (!text.Any(char.IsUpper)) return null;
            var words = Word().Matches(text.Replace('’', '\'')).Select(m => m.Value.ToLowerInvariant()).ToList();
            var rest = words.Where(w => !Honorifics.Contains(w)).ToHashSet(StringComparer.Ordinal);
            if (rest.Count == 0) return null;
            return new Form(words.Where(Honorifics.Contains).Select(Canonical).ToHashSet(StringComparer.Ordinal), rest);
        }

        /// <summary>An honorific's one spelling, so "Dr." agrees with "Doctor" and "Madame" with "Madam".</summary>
        private static string Canonical(string honorific) => honorific switch
        {
            "dr" => "doctor",
            "madame" => "madam",
            _ => honorific,
        };

        /// <summary>The forms of a name and its aliases that take part.</summary>
        public static IReadOnlyList<Form> FormsOf(IEnumerable<string> namesAndAliases) =>
            namesAndAliases.Select(FormOf).OfType<Form>().ToList();

        /// <summary>Whether any form of one side matches any form of the other: one pair is enough.</summary>
        public static bool Matches(IEnumerable<string> one, IEnumerable<string> other) => Matches(FormsOf(one), FormsOf(other));

        private static bool Matches(IReadOnlyList<Form> one, IReadOnlyList<Form> other) =>
            one.Any(a => other.Any(a.Matches));

        // ---- suggestions -------------------------------------------------------------------------

        /// <summary>
        /// A book's Character; <see cref="IsNarrator"/> marks the seed Narrator row and
        /// <see cref="SeriesCharacterId"/> its series link, if any.
        /// </summary>
        public sealed record Character(
            Guid CharacterId, string Name, IReadOnlyList<string> Aliases, bool IsNarrator, Guid? SeriesCharacterId);

        /// <summary>A member book, in Series position order; <see cref="NarratorLinked"/>: its narrator link is set.</summary>
        public sealed record Book(Guid ProjectId, bool NarratorLinked, IReadOnlyList<Character> Characters);

        /// <summary>A book's Character, by its book.</summary>
        public readonly record struct Member(Guid ProjectId, Guid CharacterId);

        /// <summary>A suggested cell: this book's unlinked Character is that Series character.</summary>
        public sealed record SuggestedCell(Guid SeriesCharacterId, Guid ProjectId, Guid CharacterId);

        /// <summary><i>Not the same</i> on a suggested cell.</summary>
        public sealed record DismissedCell(Guid SeriesCharacterId, Guid ProjectId, Guid CharacterId);

        /// <summary><i>Not the same</i> between two books' Characters, from a suggested row; either order.</summary>
        public sealed record DismissedPair(Member One, Member Other)
        {
            public bool Is(Member a, Member b) => (One == a && Other == b) || (One == b && Other == a);
        }

        /// <summary>Every <i>Not the same</i> the series keeps.</summary>
        public sealed record NotTheSame(IReadOnlyList<DismissedCell> Cells, IReadOnlyList<DismissedPair> Pairs)
        {
            public static readonly NotTheSame None = new([], []);
        }

        /// <summary>
        /// The board's suggestions: <see cref="Cells"/>, <see cref="Rows"/> (members in book order) and,
        /// by unlinked Character, the Series characters its names match, for an ambiguous cell's picker.
        /// </summary>
        public sealed record Suggestions(
            IReadOnlyList<SuggestedCell> Cells,
            IReadOnlyList<IReadOnlyList<Member>> Rows,
            IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> NameMatches)
        {
            /// <summary>What the Characters tab counts: suggested cells and suggested rows.</summary>
            public int Count => Cells.Count + Rows.Count;
        }

        private sealed record Side(IReadOnlyList<Form> Forms, bool IsNarrator)
        {
            public bool Matches(Side other) =>
                IsNarrator ? other.IsNarrator : !other.IsNarrator && SeriesNameMatch.Matches(Forms, other.Forms);
        }

        /// <summary>
        /// The board's Link suggestions, computed live from current names and aliases. A suggested
        /// cell needs the Character to match just that Series character, and that Series character to
        /// match just that Character among the book's unlinked ones. A Series character's side is
        /// every form of every Character linked to it; it is a narrator when one of them is the seed
        /// Narrator row. A book whose narrator link is set offers no seed Narrator row. A Character
        /// matching no Series character can join a suggested row: unlinked Characters of two or more
        /// books, each pair matching one-to-one.
        /// </summary>
        public static Suggestions Suggest(IReadOnlyList<Book> books, NotTheSame notTheSame)
        {
            static Side SideOf(Character c) => new(c.IsNarrator ? [] : FormsOf(c.Aliases.Prepend(c.Name)), c.IsNarrator);

            var linkedIn = new Dictionary<Guid, HashSet<Guid>>();
            var seriesSides = new Dictionary<Guid, Side>();
            foreach (var book in books)
                foreach (var c in book.Characters)
                    if (c.SeriesCharacterId is { } sc)
                    {
                        (linkedIn.TryGetValue(sc, out var set) ? set : linkedIn[sc] = []).Add(book.ProjectId);
                        var side = SideOf(c);
                        seriesSides[sc] = seriesSides.TryGetValue(sc, out var had)
                            ? new Side([.. had.Forms, .. side.Forms], had.IsNarrator || side.IsNarrator)
                            : side;
                    }

            var unlinked = books.ToDictionary(
                b => b.ProjectId,
                b => b.Characters
                    .Where(c => c.SeriesCharacterId is null && !(c.IsNarrator && b.NarratorLinked))
                    .Select(c => (Character: c, Side: SideOf(c)))
                    .ToList());

            // Each unlinked Character's matches among the Series characters with an empty cell in its book.
            var nameMatches = new Dictionary<Guid, IReadOnlyList<Guid>>();
            foreach (var book in books)
                foreach (var (c, side) in unlinked[book.ProjectId])
                {
                    var matches = seriesSides
                        .Where(s => !linkedIn[s.Key].Contains(book.ProjectId) && side.Matches(s.Value))
                        .Select(s => s.Key)
                        .ToList();
                    if (matches.Count > 0) nameMatches[c.CharacterId] = matches;
                }

            var cells = new List<SuggestedCell>();
            foreach (var book in books)
                foreach (var sc in seriesSides.Keys)
                {
                    var candidates = unlinked[book.ProjectId]
                        .Where(u => nameMatches.TryGetValue(u.Character.CharacterId, out var m) && m.Contains(sc))
                        .ToList();
                    if (candidates is not [var only] || nameMatches[only.Character.CharacterId].Count != 1) continue;
                    var id = only.Character.CharacterId;
                    if (notTheSame.Cells.Any(d => d.SeriesCharacterId == sc && d.ProjectId == book.ProjectId && d.CharacterId == id)) continue;
                    cells.Add(new SuggestedCell(sc, book.ProjectId, id));
                }

            return new Suggestions(cells, Rows(books, unlinked, nameMatches, notTheSame), nameMatches);
        }

        /// <summary>
        /// The suggested rows: Characters matching no Series character, joined across books where a
        /// pair matches one-to-one (each the only match of the other in its book) and wasn't dismissed.
        /// A group spanning two or more books, one Character in each, is a row.
        /// </summary>
        private static List<IReadOnlyList<Member>> Rows(
            IReadOnlyList<Book> books,
            IReadOnlyDictionary<Guid, List<(Character Character, Side Side)>> unlinked,
            IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> nameMatches,
            NotTheSame notTheSame)
        {
            var pool = books.ToDictionary(
                b => b.ProjectId,
                b => unlinked[b.ProjectId].Where(u => !nameMatches.ContainsKey(u.Character.CharacterId)).ToList());

            var group = new Dictionary<Member, Member>();
            Member Root(Member m)
            {
                while (group.TryGetValue(m, out var parent) && parent != m) m = parent;
                return m;
            }

            for (var i = 0; i < books.Count; i++)
                for (var j = i + 1; j < books.Count; j++)
                {
                    var (a, b) = (books[i].ProjectId, books[j].ProjectId);
                    foreach (var one in pool[a])
                    {
                        var hits = pool[b].Where(o => one.Side.Matches(o.Side)).ToList();
                        if (hits is not [var other]) continue;
                        if (pool[a].Count(o => other.Side.Matches(o.Side)) != 1) continue;
                        Member m = new(a, one.Character.CharacterId), n = new(b, other.Character.CharacterId);
                        if (notTheSame.Pairs.Any(p => p.Is(m, n))) continue;
                        group.TryAdd(m, m);
                        group.TryAdd(n, n);
                        group[Root(n)] = Root(m);
                    }
                }

            var order = books.Select((b, i) => (b.ProjectId, i)).ToDictionary(x => x.ProjectId, x => x.i);
            return group.Keys
                .GroupBy(Root)
                .Select(g => g.OrderBy(m => order[m.ProjectId]).ToList())
                .Where(g => g.Count >= 2 && g.Select(m => m.ProjectId).Distinct().Count() == g.Count)
                .OrderBy(g => order[g[0].ProjectId])
                .Select(g => (IReadOnlyList<Member>)g)
                .ToList();
        }

        /// <summary>A word: letters and digits, with apostrophes and hyphens kept inside it.</summary>
        [GeneratedRegex(@"[\p{L}\p{N}]+(?:['\-][\p{L}\p{N}]+)*")]
        private static partial Regex Word();
    }
}
