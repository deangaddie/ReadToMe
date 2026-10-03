using static Read2Me.Services.Characters.Rules.SpeechLexicon;

namespace Read2Me.Services.Characters.Rules
{
    /// <summary>A rule's answer for one dialog item: the roster name and the rule id (e.g. <c>T1-post-vs</c>).</summary>
    internal sealed record RuleTag(string Speaker, string Rule);

    /// <summary>
    /// Which tiers <see cref="SpeechTagRules.TagChapter"/> runs. All on by default; each is a
    /// one-line switch if a tier misbehaves on unseen books (spec R5).
    /// <list type="bullet">
    /// <item>T1: a named speech tag before or after the quote.</item>
    /// <item>T2: the paragraph's single speaker propagated to its untagged quotes.</item>
    /// <item>T3: a pronoun tag whose antecedent is in the same paragraph.</item>
    /// <item>T4: the action beat before a paragraph's first quote.</item>
    /// <item>T5: a two-person alternation between anchored paragraphs.</item>
    /// </list>
    /// </summary>
    internal sealed record RuleTiers(bool T1 = true, bool T2 = true, bool T3 = true, bool T4 = true, bool T5 = true)
    {
        public static readonly RuleTiers All = new();
    }

    /// <summary>
    /// The zero-LLM speaker tagger: a port of the attribution-options spike's <c>rules.mjs</c> v2
    /// <c>tagChapter</c>, kept faithful to it (a parity test compares the two item for item). It
    /// answers only the dialog items it is sure of; everything else is left to the model. The
    /// optional first-person rule is not ported (off for lab parity, spec Q6).
    /// <para>
    /// Works on each paragraph's joined text (items joined by one space), so it does not depend on
    /// how the importer split items: quote spans are found with “ ” (or straight quotes), narration is
    /// the text between quotes, and each dialog item takes the speaker of every quote span it
    /// overlaps (all must agree).
    /// </para>
    /// </summary>
    internal static partial class SpeechTagRules
    {
        /// <summary>T5's widest gap between two anchors, in dialog paragraphs.</summary>
        private const int MaxGap = 6;

        private static readonly string[] RuleStrength = ["T1", "T3", "T4", "T2"];

        /// <summary>What the passes decided for one quote.</summary>
        private sealed record QuoteResult(string? Speaker, string? Rule)
        {
            public static readonly QuoteResult Blocked = new(null, null);
        }

        private sealed record ParagraphInfo(
            ParsedParagraph P, IReadOnlyList<Span> DialogItems, ParagraphKind Kind, string? Anchor, bool Fillable,
            IReadOnlySet<string> QuoteNames);

        private enum ParagraphKind { Pause, Narr, Dialog }

        private sealed record NarrSentence(IReadOnlyList<string> W, int Before);

        /// <summary>
        /// Tags the chapter's dialog items the rules are confident about: item id → (roster name, rule).
        /// <paramref name="roster"/> must leave out the seed Narrator row.
        /// </summary>
        public static IReadOnlyDictionary<Guid, RuleTag> TagChapter(
            IReadOnlyList<ChapterParagraph> chapter, IReadOnlyList<RosterEntry> roster, RuleTiers? tiers = null)
        {
            tiers ??= RuleTiers.All;
            var idx = NameIndex.Build(roster);
            var output = new Dictionary<Guid, RuleTag>();
            var paraInfo = new List<ParagraphInfo>();

            foreach (var paragraph in chapter)
            {
                var info = TagParagraph(idx, paragraph, tiers, output);
                paraInfo.Add(info);
            }

            if (tiers.T5)
                Alternation(paraInfo, output);
            return output;
        }

        private static ParagraphInfo TagParagraph(
            NameIndex idx, ChapterParagraph paragraph, RuleTiers tiers, Dictionary<Guid, RuleTag> output)
        {
            var p = ParseParagraph(paragraph);
            var quotes = p.Quotes;
            var dialogItems = p.Spans.Where(s => s.Item.IsDialog).ToList();
            var hasDialog = dialogItems.Count > 0 && quotes.Count > 0;
            var hasSpeechItems = p.Spans.Count > 0;

            // narration sentences in order, with the quote each one precedes
            var narr = new List<NarrSentence>();
            for (var k = 0; k < quotes.Count; k++)
                foreach (var s in SplitSentences(TrimEndOpenQuotes(quotes[k].Pre)))
                    narr.Add(new NarrSentence(Tokenize(s), k));

            // pronoun antecedent: walk back over this paragraph's narration sentences (never across paragraphs)
            string? ResolvePron(string pron, int beforeQuote, bool skipLastInPre, IReadOnlyList<string> ownWords)
            {
                if (pron is not ("he" or "she"))
                    return null;
                var cand = narr.Where(n => n.Before <= beforeQuote).ToList();
                if (skipLastInPre && cand.Count > 0)
                    cand.RemoveAt(cand.Count - 1);
                for (var k = cand.Count - 1; k >= 0; k--)
                {
                    var sub = SentenceSubject(idx, cand[k].W);
                    if (sub is null)
                        return null;
                    if (sub.Kind == NpKind.Pron)
                    {
                        if (sub.P == pron)
                            continue;
                        return null;
                    }
                    if (sub.Kind == NpKind.Name)
                    {
                        if (RosterMentions(idx, cand[k].W).Any(n => n != sub.Name))
                            return null;
                        // gender guard: an antecedent sentence carrying an opposite-gender pronoun is ambiguous
                        var opposite = pron == "he" ? Fem : Masc;
                        if (cand[k].W.Any(x => opposite.Contains(Lc(x))))
                            return null;
                        // "He turned to Pug" cannot be Pug
                        if (RosterMentions(idx, ownWords).Contains(sub.Name!))
                            return null;
                        return sub.Name;
                    }
                    return null;
                }
                return null;
            }

            // a quote embedded mid-sentence in narration (I was “dog-tired,” and …) is not a turn
            foreach (var q in quotes)
            {
                var pre = TrimEndOpenQuotes(q.Pre);
                q.Embedded = IsLowerEnd(pre) && !SpeechVerbs.Contains(Lc(LastWord(pre)));
            }

            bool MentionsInQuote(Quote q, string name) =>
                RosterMentions(idx, Tokenize(StripQuoteMarks(q.Text))).Contains(name);

            var res = new QuoteResult?[quotes.Count];
            var blockers = new List<int>();

            // pass 1: explicit tags (T1 named, T3 pronoun)
            for (var k = 0; k < quotes.Count; k++)
            {
                var q = quotes[k];
                var tags = new List<TagHit>();
                var pt = PostTag(idx, q);
                if (pt is not null)
                    tags.Add(pt);
                var pr = PreTag(idx, q);
                if (pr is not null)
                    tags.Add(pr);
                q.Tagged = tags.Count > 0;
                q.PostTagged = pt is not null;

                string? speaker = null, rule = null;
                var blocked = false;
                foreach (var t in tags)
                {
                    if (t.Np.Kind == NpKind.Name)
                    {
                        if (speaker is not null && speaker != t.Np.Name)
                            blocked = true;
                        speaker = t.Np.Name;
                        rule = "T1-" + t.Form;
                    }
                    else if (t.Np.Kind == NpKind.Unres)
                        blocked = true;
                    else if (t.Np.Kind == NpKind.Pron && t.Np.P == "i")
                        q.PronUnres = "i"; // first person: needs a point-of-view character (not ported)
                    else if (t.Np.Kind == NpKind.Pron)
                    {
                        var own = Tokenize(t.Form == "pre"
                            ? SplitSentences(q.Pre).LastOrDefault() ?? ""
                            : SplitSentences(TrimStartCommas(q.Post)).FirstOrDefault() ?? "");
                        string? nm;
                        // "Kulgan lit his pipe, and once he was satisfied …, he said,": the antecedent is the tag sentence's own subject
                        var ownSub = t.Form == "pre" ? SentenceSubject(idx, own) : null;
                        var ownOpposite = t.Np.P == "he" ? Fem : Masc;
                        if (ownSub is { Kind: NpKind.Name }
                            && !RosterMentions(idx, own).Any(n => n != ownSub.Name)
                            && !own.Any(x => ownOpposite.Contains(Lc(x))))
                            nm = ownSub.Name;
                        else
                            nm = ResolvePron(t.Np.P!, k, t.Form == "pre", own);

                        if (nm is not null && tiers.T3)
                        {
                            if (speaker is not null && speaker != nm)
                                blocked = true;
                            if (speaker is null)
                            {
                                speaker = nm;
                                rule = "T3-pron-" + t.Form;
                            }
                        }
                        else if (nm is null)
                            q.PronUnres = t.Np.P;
                    }
                }
                if (blocked)
                {
                    blockers.Add(k);
                    res[k] = QuoteResult.Blocked;
                    continue;
                }
                if (speaker is not null && rule!.StartsWith("T1", StringComparison.Ordinal) && !tiers.T1)
                    speaker = null;
                if (speaker is not null)
                    res[k] = new QuoteResult(speaker, rule);
            }

            // pass 2: action beats (the subject of the narration sentence right before an untagged quote)
            for (var k = 0; k < quotes.Count; k++)
            {
                var q = quotes[k];
                if (q.Tagged)
                    continue;
                var pre = TrimEndOpenQuotes(q.Pre);
                if (JsTrim(pre).Length == 0)
                    continue;
                var sents = SplitSentences(pre);
                var w = Tokenize(sents.Count > 0 ? sents[^1] : "");
                var sub = SentenceSubject(idx, w);
                if (sub is null)
                    continue;
                var vi = w.FindIndex(x => Lc(x) is "voice" or "voices");
                var voiceOk = vi > 0 && IsPoss(w[vi - 1]) && sub.Kind == NpKind.Name; // "Laurie’s voice brought him …"
                if (sub.Kind == NpKind.Unres || (vi >= 0 && !voiceOk))
                {
                    q.BeatOther = true;
                    continue;
                }
                string? nm = null;
                if (sub.Kind == NpKind.Name)
                    nm = RosterMentions(idx, w).Any(n => n != sub.Name) ? null : sub.Name;
                else if (sub.Kind == NpKind.Pron)
                    nm = ResolvePron(sub.P!, k, true, w);
                if (nm is null)
                {
                    q.BeatUnres = true;
                    continue;
                }
                if (k > 0)
                {
                    q.MidBeat = nm; // mid-paragraph beats are often listener reactions: never an answer
                    continue;
                }
                if (tiers.T4 && blockers.Count == 0 && !q.Embedded && !MentionsInQuote(q, nm))
                    res[k] = new QuoteResult(nm, "T4-beat");
            }

            // T2: propagate a single paragraph speaker to unresolved quotes
            var spk = res.Where(r => r?.Speaker is not null).Select(r => r!.Speaker!).ToHashSet(StringComparer.Ordinal);
            var midOthers = quotes.Any(q => q.MidBeat is not null && !spk.Contains(q.MidBeat));
            // an unresolved pronoun tag in a different grammatical person ("I said" vs "he said") is a second speaker
            var personClash = quotes.Any(q => q.PronUnres == "i");
            var single = spk.Count == 1 && blockers.Count == 0 && !personClash && !midOthers;
            if (tiers.T2 && single)
            {
                var s = spk.First();
                var first = Array.FindIndex(res, r => r?.Speaker is not null);
                bool TagOnly(int j) =>
                    SplitSentences(TrimEndOpenQuotes(quotes[j].Pre)).Count <= 1 && quotes[j - 1].PostTagged;
                for (var k = 0; k < quotes.Count; k++)
                {
                    var q = quotes[k];
                    if (res[k] is not null || q.BeatOther || q.Embedded || MentionsInQuote(q, s))
                        continue;
                    if (k < first && !Enumerable.Range(k + 1, first - k).All(TagOnly))
                        continue;
                    res[k] = new QuoteResult(s, "T2-prop");
                }
            }

            // quotes → items
            foreach (var span in dialogItems)
            {
                var rs = quotes.Select((q, k) => (q, k)).Where(x => x.q.Overlaps(span)).Select(x => res[x.k]).ToList();
                if (rs.Count == 0 || rs.Any(r => r?.Speaker is null))
                    continue;
                if (rs.Select(r => r!.Speaker).Distinct(StringComparer.Ordinal).Count() != 1)
                    continue;
                // the weakest rule involved names the tag
                var rule = rs.Select(r => r!.Rule!)
                    .OrderByDescending(r => Array.IndexOf(RuleStrength, r[..2]))
                    .First();
                output[span.Item.ItemId] = new RuleTag(rs[0]!.Speaker!, rule);
            }

            // paragraph summary for T5
            var allQuoted = quotes.Count > 0;
            var paraSpeakers = res.Where(r => r?.Speaker is not null).Select(r => r!.Speaker!).Distinct(StringComparer.Ordinal).ToList();
            var fullyKnown = res.Length > 0 && res.All(r => r?.Speaker is not null);
            return new ParagraphInfo(
                p,
                dialogItems,
                !hasSpeechItems ? ParagraphKind.Pause : !hasDialog ? ParagraphKind.Narr : ParagraphKind.Dialog,
                allQuoted && paraSpeakers.Count == 1 && fullyKnown ? paraSpeakers[0] : null,
                allQuoted && !quotes.Any(q => q.Embedded) && paraSpeakers.Count == 0 && blockers.Count == 0
                    && !quotes.Any(q => q.BeatOther || q.PronUnres is not null || q.BeatUnres || q.MidBeat is not null),
                quotes.SelectMany(q => RosterMentions(idx, Tokenize(StripQuoteMarks(q.Text)))).ToHashSet(StringComparer.Ordinal));
        }

        /// <summary>T5: a two-person alternation between anchored paragraphs in an unbroken run of dialog paragraphs.</summary>
        private static void Alternation(IReadOnlyList<ParagraphInfo> paraInfo, Dictionary<Guid, RuleTag> output)
        {
            var runs = new List<List<ParagraphInfo>>();
            var cur = new List<ParagraphInfo>();
            foreach (var pi in paraInfo)
            {
                if (pi.Kind == ParagraphKind.Pause)
                    continue;
                if (pi.Kind == ParagraphKind.Narr)
                {
                    if (cur.Count > 0)
                        runs.Add(cur);
                    cur = [];
                    continue;
                }
                cur.Add(pi);
            }
            if (cur.Count > 0)
                runs.Add(cur);

            foreach (var run in runs)
            {
                var anchors = run.Select((pi, i) => (I: i, S: pi.Anchor)).Where(a => a.S is not null).ToList();
                for (var a = 0; a + 1 < anchors.Count; a++)
                {
                    var x = anchors[a];
                    var y = anchors[a + 1];
                    if (y.I - x.I < 2 || y.I - x.I > MaxGap + 1)
                        continue;
                    // the speakers of both parities: from x and y, or from a neighbouring anchor
                    var parity = new Dictionary<int, string>();
                    bool Add((int I, string? S) an)
                    {
                        var k = an.I % 2;
                        if (parity.TryGetValue(k, out var had) && had != an.S)
                            return false;
                        parity[k] = an.S!;
                        return true;
                    }
                    var ok = Add(x) && Add(y);
                    if (ok && parity.Count < 2)
                    {
                        var neighbours = new[] { a - 1, a + 2 }
                            .Where(n => n >= 0 && n < anchors.Count)
                            .Select(n => anchors[n])
                            .Where(n => Math.Abs(n.I - (n.I < x.I ? x.I : y.I)) <= MaxGap + 1);
                        foreach (var n in neighbours)
                        {
                            if (n.I % 2 != x.I % 2)
                            {
                                ok = Add(n);
                                break;
                            }
                        }
                    }
                    if (!ok || parity.Count < 2 || parity[0] == parity[1])
                        continue;
                    if (run.Skip(x.I + 1).Take(y.I - x.I - 1).Any(pi => !pi.Fillable && pi.Anchor is null))
                        continue;
                    for (var k = x.I + 1; k < y.I; k++)
                    {
                        var pi = run[k];
                        if (pi.Anchor is not null)
                            continue;
                        var s = parity[k % 2];
                        if (pi.QuoteNames.Contains(s))
                            continue;
                        foreach (var span in pi.DialogItems)
                            if (!output.ContainsKey(span.Item.ItemId) && pi.P.Quotes.Any(q => q.Overlaps(span)))
                                output[span.Item.ItemId] = new RuleTag(s, "T5-alt");
                    }
                }
            }
        }

        private static bool IsLowerEnd(string s) => s.Length > 0 && s[^1] is >= 'a' and <= 'z';

        /// <summary><c>split(/\s+/).pop()</c>.</summary>
        private static string LastWord(string s)
        {
            var end = s.Length;
            var start = end;
            while (start > 0 && !IsJsSpace(s[start - 1]))
                start--;
            return s[start..end];
        }

        /// <summary><c>replace(/[“”"]/g, ' ')</c>.</summary>
        private static string StripQuoteMarks(string s) =>
            s.Replace('“', ' ').Replace('”', ' ').Replace('"', ' ');
    }
}
