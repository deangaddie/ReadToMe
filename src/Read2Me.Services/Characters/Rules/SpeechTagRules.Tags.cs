using System.Text.RegularExpressions;
using static Read2Me.Services.Characters.Rules.SpeechLexicon;

namespace Read2Me.Services.Characters.Rules
{
    /// <summary>
    /// Paragraph parsing, noun-phrase analysis and tag detection (the prototype's everything between
    /// the name index and <c>tagChapter</c>). Shared by <see cref="TagChapter"/> and name discovery, which reads the <c>unknown:</c> mentions of <see cref="PostTag"/>
    /// and <see cref="PreTag"/>.
    /// </summary>
    internal static partial class SpeechTagRules
    {
        private enum NpKind { Name, Pron, Unres, Coord }

        /// <summary>
        /// A noun phrase: a roster <see cref="Name"/>, a pronoun <see cref="P"/>, an unresolved
        /// speaker phrase (<see cref="Why"/>, e.g. <c>unknown:Laurie</c>), or a coordinator.
        /// </summary>
        private sealed record Np(NpKind Kind, string? Name = null, string? P = null, string? Why = null)
        {
            public static readonly Np Coord = new(NpKind.Coord);

            public static Np Unres(string why) => new(NpKind.Unres, Why: why);
        }

        /// <summary>A tag found next to a quote, and where: <c>post-vs</c>, <c>post-sv</c> or <c>pre</c>.</summary>
        private sealed record TagHit(Np Np, string Form);

        private sealed record Span(ContextItem Item, int S, int E);

        /// <summary>One quote span of a paragraph's joined text, with the narration around it and what the passes found.</summary>
        private sealed class Quote(int s, int e)
        {
            public int S { get; } = s;
            public int E { get; } = e;
            public string Text { get; set; } = "";
            public string Pre { get; set; } = "";
            public string Post { get; set; } = "";
            public bool Embedded { get; set; }
            public bool Tagged { get; set; }
            public bool PostTagged { get; set; }
            public bool BeatOther { get; set; }
            public bool BeatUnres { get; set; }
            public string? PronUnres { get; set; }
            public string? MidBeat { get; set; }

            public bool Overlaps(Span span) => S < span.E && E > span.S;
        }

        private sealed record ParsedParagraph(string Text, IReadOnlyList<Span> Spans, IReadOnlyList<Quote> Quotes);

        /// <summary>
        /// The paragraph's items joined by one space, the item spans, and the quote spans (“ ” or
        /// straight quotes; a new “ before a close ends the open quote unclosed).
        /// </summary>
        private static ParsedParagraph ParseParagraph(ChapterParagraph paragraph)
        {
            var text = "";
            var spans = new List<Span>();
            foreach (var item in paragraph.Items)
            {
                var s = text.Length > 0 ? text.Length + 1 : 0;
                text += (text.Length > 0 ? " " : "") + item.Text;
                spans.Add(new Span(item, s, text.Length));
            }

            var quotes = new List<Quote>();
            var open = -1;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (open < 0 && c is '“' or '"')
                    open = i;
                else if (open >= 0 && (c == '”' || (c == '"' && text[open] == '"')))
                {
                    quotes.Add(new Quote(open, i + 1));
                    open = -1;
                }
                else if (open >= 0 && c == '“')
                {
                    quotes.Add(new Quote(open, i));
                    open = i;
                }
            }
            if (open >= 0)
                quotes.Add(new Quote(open, text.Length));

            for (var k = 0; k < quotes.Count; k++)
            {
                var q = quotes[k];
                q.Text = Slice(text, q.S, q.E);
                q.Pre = Slice(text, k > 0 ? quotes[k - 1].E : 0, q.S);
                q.Post = Slice(text, q.E, k + 1 < quotes.Count ? quotes[k + 1].S : text.Length);
            }
            return new ParsedParagraph(text, spans, quotes);
        }

        /// <summary>JavaScript <c>slice</c>: an empty string when the range is reversed.</summary>
        private static string Slice(string s, int start, int end) => end > start ? s[start..end] : "";

        private static string? At(IReadOnlyList<string> w, int i) => i >= 0 && i < w.Count ? w[i] : null;

        private static string LcAt(IReadOnlyList<string> w, int i) => Lc(At(w, i) ?? "");

        /// <summary>Capitalised name run starting at <paramref name="i"/> going forward (honorifics allowed; ends after a possessive). Returns its end.</summary>
        private static int CapRunFwd(IReadOnlyList<string> w, int i)
        {
            var j = i;
            while (j < w.Count && IsCap(w[j]) && !IsPunct(w[j]))
            {
                j++;
                if (IsPoss(w[j - 1]))
                    break;
            }
            return j;
        }

        private static List<string> Range(IReadOnlyList<string> w, int start, int end) =>
            [.. w.Skip(start).Take(Math.Max(0, end - start))];

        /// <summary>A capitalised mention, allowing one leading capitalised common word to be dropped ("Suddenly Pug").</summary>
        private static Np NpFromMention(NameIndex idx, IReadOnlyList<string> toks)
        {
            var r = idx.Match(toks);
            if (r.Status != NameMatchStatus.Roster && toks.Count >= 2 && !Honorifics.Contains(Strip(toks[0])))
            {
                var r2 = idx.Match([.. toks.Skip(1)]);
                if (r2.Status == NameMatchStatus.Roster)
                    r = r2;
            }
            return r.Status == NameMatchStatus.Roster
                ? new Np(NpKind.Name, Name: r.Name)
                : Np.Unres(r.StatusWord + ":" + string.Join(" ", toks));
        }

        /// <summary>The noun phrase starting at <paramref name="i"/>.</summary>
        private static Np? NpForward(NameIndex idx, IReadOnlyList<string> w, int i)
        {
            if (i >= w.Count)
                return null;
            var t = w[i];
            var l = Lc(t);
            if (Pron.Contains(l))
                return new Np(NpKind.Pron, P: l);
            if (IsCap(t) && !Det.Contains(l))
            {
                var j = CapRunFwd(w, i);
                var toks = Range(w, i, j);
                if (IsPoss(toks[^1]))
                {
                    if (LcAt(w, j) == "voice")
                        return NpFromMention(idx, [.. toks.Select(DropPossessive)]);
                    return Np.Unres("possessive");
                }
                if (LcAt(w, j) == "and"
                    && (IsCap(At(w, j + 1) ?? "") || Det.Contains(LcAt(w, j + 1)) || Pron.Contains(LcAt(w, j + 1))))
                    return Np.Unres("compound");
                return NpFromMention(idx, toks);
            }
            if (Det.Contains(l))
            {
                var ph = new List<string>();
                var k = i + 1;
                while (k < w.Count && ph.Count < 4)
                {
                    var x = w[k];
                    var lx = Lc(x);
                    if (IsPunct(x) || IsCap(x) || Prep.Contains(lx) || NpStop.Contains(lx) || IsAdverb(lx)
                        || SpeechVerbs.Contains(lx) || x.EndsWith("ed", StringComparison.Ordinal))
                        break;
                    ph.Add(x);
                    k++;
                    if (IsPoss(x))
                        break;
                }
                if (ph.Count == 0)
                    return null;
                if (IsPoss(ph[^1]))
                    return Np.Unres("possessive-np");
                if (LcAt(w, k) == "and")
                    return Np.Unres("compound");
                var r = idx.Noun(ph);
                return r.Status == NameMatchStatus.Roster
                    ? new Np(NpKind.Name, Name: r.Name)
                    : Np.Unres("np:" + l + " " + string.Join(" ", ph));
            }
            return null;
        }

        /// <summary>The noun phrase ending at <paramref name="j"/>, going backwards.</summary>
        private static Np? NpBackward(NameIndex idx, IReadOnlyList<string> w, int j)
        {
            while (j >= 0 && IsAdverb(Lc(w[j])) && Lc(w[j]) != "then")
                j--;
            if (j < 0)
                return null;
            var t = w[j];
            var l = Lc(t);
            if (l is "and" or "then" or "but" || t == ",")
                return Np.Coord;
            if (Pron.Contains(l))
                return Prep.Contains(LcAt(w, j - 1)) ? null : new Np(NpKind.Pron, P: l);
            if (IsCap(t) && !IsPunct(t))
            {
                var s = j;
                while (s - 1 >= 0 && IsCap(w[s - 1]) && !IsPunct(w[s - 1]) && !IsPoss(w[s - 1]))
                    s--;
                var prev = LcAt(w, s - 1);
                if (Prep.Contains(prev))
                    return null; // object: "then to Laurie said"
                if (prev == "and")
                    return Np.Unres("compound");
                return NpFromMention(idx, Range(w, s, j + 1));
            }
            if (IsLowerStart(t))
            {
                if (l == "voice" && IsPoss(At(w, j - 1) ?? "") && IsCap(At(w, j - 1)))
                {
                    var s = j - 1;
                    while (s - 1 >= 0 && IsCap(w[s - 1]) && !IsPunct(w[s - 1]))
                        s--;
                    return NpFromMention(idx, [.. Range(w, s, j).Select(DropPossessive)]);
                }
                for (var k = j - 1; k >= Math.Max(0, j - 4); k--)
                {
                    var x = Lc(w[k]);
                    if (Det.Contains(x))
                    {
                        if (Prep.Contains(LcAt(w, k - 1)))
                            return null;
                        var ph = Range(w, k + 1, j + 1);
                        if (ph.Any(IsPunct))
                            return null;
                        if (LcAt(w, k - 1) == "and")
                            return Np.Unres("compound");
                        var r = idx.Noun(ph);
                        return r.Status == NameMatchStatus.Roster
                            ? new Np(NpKind.Name, Name: r.Name)
                            : Np.Unres("np:" + x + " " + string.Join(" ", ph));
                    }
                    if (IsPunct(w[k]) || IsCap(w[k]))
                        break;
                }
            }
            return null;
        }

        /// <summary>The grammatical subject of a narration sentence (heuristic): after a leading clause or opener word.</summary>
        private static Np? SentenceSubject(NameIndex idx, IReadOnlyList<string> w)
        {
            if (w.Count == 0)
                return null;
            var i = 0;
            var f = Lc(w[0]);
            if (LeadClause.Contains(f) || CapIng().IsMatch(w[0]) || CapLy().IsMatch(w[0]))
            {
                var c = IndexOf(w, ",");
                if (c < 0 || c > 14)
                    return null;
                i = c + 1;
            }
            else if (LeadWord.Contains(f))
            {
                i = 1;
                if (At(w, 1) == ",")
                    i = 2;
            }
            return NpForward(idx, w, i);
        }

        private static int IndexOf(IReadOnlyList<string> w, string token)
        {
            for (var i = 0; i < w.Count; i++)
                if (w[i] == token)
                    return i;
            return -1;
        }

        /// <summary>Roster names mentioned anywhere in <paramref name="w"/>, in order.</summary>
        private static List<string> RosterMentions(NameIndex idx, IReadOnlyList<string> w)
        {
            var output = new List<string>();
            for (var i = 0; i < w.Count;)
            {
                if (IsCap(w[i]) && !IsPunct(w[i]))
                {
                    var j = CapRunFwd(w, i);
                    var r = NpFromMention(idx, [.. Range(w, i, j).Select(DropPossessive)]);
                    if (r.Kind == NpKind.Name)
                        output.Add(r.Name!);
                    i = j;
                }
                else
                    i++;
            }
            return output;
        }

        /// <summary>A tag after a quote: <c>“…,” said Pug</c> / <c>Pug said</c> / <c>he said</c> / <c>the man said</c>.</summary>
        private static TagHit? PostTag(NameIndex idx, Quote q)
        {
            var frag = TrimStartCommas(q.Post);
            if (frag.Length == 0)
                return null;
            var w = Tokenize(SplitSentences(frag).FirstOrDefault() ?? "");
            if (w.Count == 0)
                return null;
            var endsSoft = SoftQuoteEnd().IsMatch(q.Text) || IsLowerStart(frag);

            // verb-first: said Pug / said his lady / returned she
            var i = 0;
            while (i < w.Count && IsAdverb(Lc(w[i])) && Lc(w[i]) != "then")
                i++;
            if (SpeechVerbs.Contains(LcAt(w, i)) && IsLowerStart(w[i]) && endsSoft)
            {
                var vs = NpForward(idx, w, i + 1);
                return vs is null ? null : new TagHit(vs, "post-vs");
            }

            // subject-first: Pug said / he said / the magician exclaimed
            var np = NpForward(idx, w, 0);
            if (np is null)
                return null;
            var j = 1;
            if (np.Kind != NpKind.Pron)
            {
                if (IsCap(w[0]) && !Det.Contains(Lc(w[0])))
                    j = CapRunFwd(w, 0);
                else
                {
                    j = 1;
                    while (j < w.Count && j < 6 && !SpeechVerbs.Contains(Lc(w[j])) && !IsPunct(w[j]) && !IsAdverb(Lc(w[j])))
                        j++;
                }
            }
            while (j < w.Count && IsAdverb(Lc(w[j])))
                j++;
            if (!SpeechVerbs.Contains(LcAt(w, j)))
                return null;
            // reported speech ("Pug said he would", "said nothing") is not a tag
            if (Reported.Contains(LcAt(w, j + 1)))
                return null;
            if (np.Kind == NpKind.Pron && !endsSoft)
                return null;
            if (np.Kind != NpKind.Pron && !endsSoft && !IsCap(w[0]))
                return null;
            return new TagHit(np, "post-sv");
        }

        /// <summary>A tag before a quote: <c>Kulgan said, “…”</c> / <c>Pug nodded and said, “…”</c>.</summary>
        private static TagHit? PreTag(NameIndex idx, Quote q)
        {
            var frag = TrimEndOpenQuotes(q.Pre);
            if (frag.Length == 0 || frag[^1] is not (',' or ':'))
                return null;
            var sents = SplitSentences(frag);
            var w = Tokenize(sents.Count > 0 ? sents[^1] : "");
            var v = -1;
            for (var k = w.Count - 1; k >= 0; k--)
            {
                if (SpeechVerbs.Contains(Lc(w[k])) && IsLowerStart(w[k]))
                {
                    v = k;
                    break;
                }
            }
            if (v < 0)
                return null;
            // a person mention after the verb that is not an object of a preposition => a different clause
            for (var k = v + 1; k < w.Count; k++)
            {
                var x = w[k];
                var person = Pron.Contains(Lc(x))
                    || (IsCap(x) && !IsPunct(x) && idx.Match([DropPossessive(x)]).Status != NameMatchStatus.Unknown);
                if (person && !(Prep.Contains(LcAt(w, k - 1)) || Prep.Contains(LcAt(w, k - 2)) || IsPoss(x)))
                    return null;
            }
            var np = NpBackward(idx, w, v - 1);
            if (np is { Kind: NpKind.Coord })
                np = SentenceSubject(idx, Range(w, 0, v));
            return np is null ? null : new TagHit(np, "pre");
        }

        [GeneratedRegex("^[A-Z][a-z]+ing\\z", RegexOptions.CultureInvariant)]
        private static partial Regex CapIng();

        [GeneratedRegex("^[A-Z][a-z]+ly\\z", RegexOptions.CultureInvariant)]
        private static partial Regex CapLy();

        /// <summary><c>/[,!?—–…]\s*[”"]$/</c> with ECMAScript whitespace.</summary>
        [GeneratedRegex("[,!?—–…][\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff]*[”\"]\\z", RegexOptions.CultureInvariant)]
        private static partial Regex SoftQuoteEnd();
    }
}
