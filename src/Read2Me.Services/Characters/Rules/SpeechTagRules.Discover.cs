using System.Collections.Frozen;
using static Read2Me.Services.Characters.Rules.SpeechLexicon;

namespace Read2Me.Services.Characters.Rules
{
    /// <summary>
    /// A capitalised speaker name a speech tag uses that no roster name or alias answers to: how often
    /// a tag names it in the chapter, and the narration around its first tag (at most 80 chars).
    /// </summary>
    internal sealed record DiscoveredName(string Name, int Count, string Example);

    /// <summary>Rules-discover (the prototype's <c>discoverNames</c>): names to add to the roster before a chapter is asked.</summary>
    internal static partial class SpeechTagRules
    {
        private const int ExampleLength = 80;
        private const string UnknownPrefix = "unknown:";

        /// <summary>Words a tag phrase may start with that are not part of a name (the prototype's <c>OPENERS</c>).</summary>
        private static readonly FrozenSet<string> Openers = FrozenSet.ToFrozenSet(
            [.. LeadWord, .. LeadClause, "yes", "no", "oh", "ah", "well", "very", "even", "instead", "perhaps", "only", "all", "i"],
            StringComparer.Ordinal);

        /// <summary>
        /// The names the chapter's speech tags use that the roster does not know, in first-seen order
        /// (case-insensitively distinct). A tag's mention loses leading openers and <c>-ly</c> adverbs
        /// ("said Suddenly Laurie"); a mention of honorifics only ("said Lord") and one sharing a
        /// non-honorific word with any roster name or alias ("Lord Borric" beside "Duke Borric") are
        /// skipped. <paramref name="roster"/> must leave out the seed Narrator row.
        /// </summary>
        public static IReadOnlyList<DiscoveredName> DiscoverNames(
            IReadOnlyList<ChapterParagraph> chapter, IReadOnlyList<RosterEntry> roster)
        {
            var idx = NameIndex.Build(roster);
            var found = new List<DiscoveredName>();
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var paragraph in chapter)
                foreach (var q in ParseParagraph(paragraph).Quotes)
                    foreach (var hit in (TagHit?[])[PostTag(idx, q), PreTag(idx, q)])
                    {
                        if (UnknownMention(hit) is not { } mention || NameFrom(idx, mention) is not { } name)
                            continue;
                        var key = Lc(name);
                        if (byKey.TryGetValue(key, out var at))
                            found[at] = found[at] with { Count = found[at].Count + 1 };
                        else
                        {
                            byKey[key] = found.Count;
                            found.Add(new DiscoveredName(name, 1, Example(q)));
                        }
                    }
            return found;
        }

        /// <summary>The mention tokens of an unresolved tag the name index did not know at all.</summary>
        private static string[]? UnknownMention(TagHit? hit) =>
            hit?.Np is { Kind: NpKind.Unres, Why: { } why } && why.StartsWith(UnknownPrefix, StringComparison.Ordinal)
                ? [.. why[UnknownPrefix.Length..].Split(' ').Where(t => t.Length > 0).Select(DropPossessive)]
                : null;

        private static string? NameFrom(NameIndex idx, string[] mention)
        {
            var toks = mention.SkipWhile(t => Openers.Contains(Lc(t)) || CapLy().IsMatch(t)).ToList();
            if (toks.Count == 0 || toks.All(t => Honorifics.Contains(Strip(t))))
                return null;
            if (toks.Any(t => !Honorifics.Contains(Strip(t)) && idx.CapWords.Contains(Strip(t))))
                return null;
            return string.Join(" ", toks);
        }

        /// <summary>The narration after the quote, or before it when there is none.</summary>
        private static string Example(Quote q)
        {
            var text = JsTrim(q.Post.Length > 0 ? q.Post : q.Pre);
            return text.Length > ExampleLength ? text[..ExampleLength] : text;
        }
    }
}
