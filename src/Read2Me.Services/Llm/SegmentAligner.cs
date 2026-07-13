namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Maps LLM segment boundaries onto the original paragraph text. The output segments carry
    /// slices of the original — concatenating exactly back to it — so LLM character drift can
    /// never corrupt book text. Matching is normalization-tolerant (see
    /// <see cref="SegmentTextNormalizer"/>) plus boundary-tolerant: at each segment join up to one
    /// unclaimed punctuation character in the original is absorbed into the preceding slice, and a
    /// segment's own stray leading (1) / trailing (2) punctuation characters are consumed — the
    /// dominant trial failure was ±1 punctuation character at joins. Letters and digits are never
    /// skipped, so real in-segment omissions or duplications still fail.
    /// </summary>
    internal static class SegmentAligner
    {
        /// <summary>
        /// (original chars skipped, segment leading chars dropped, segment trailing chars dropped)
        /// tried per segment in ascending total-drift order, exact match first.
        /// </summary>
        private static readonly (int SkipOriginal, int DropLeading, int DropTrailing)[] Candidates =
            (from skip in new[] { 0, 1 }
             from lead in new[] { 0, 1 }
             from trail in new[] { 0, 1, 2 }
             orderby skip + lead + trail
             select (skip, lead, trail)).ToArray();

        public static bool TryAlign(
            string originalText, IReadOnlyList<AttributionSegment> segments,
            out IReadOnlyList<AttributionSegment> aligned)
        {
            aligned = default!;
            if (segments == null || segments.Count == 0 || string.IsNullOrWhiteSpace(originalText))
                return false;

            var normalized = SegmentTextNormalizer.Build(originalText, out var map);
            var segmentTexts = new string[segments.Count];
            for (var i = 0; i < segments.Count; i++)
            {
                segmentTexts[i] = SegmentTextNormalizer.Normalize(segments[i].Text);
                if (segmentTexts[i].Length == 0)
                    return false;
            }

            // starts[i] = normalized position where segment i's matched text begins.
            var starts = new int[segments.Count];
            if (!Match(normalized, segmentTexts, 0, 0, starts))
                return false;

            // Slice the original at the match-start boundaries: unclaimed join characters fall to
            // the preceding slice, paragraph leading/trailing characters to the first/last — the
            // slices always concatenate back to the exact original text.
            var result = new AttributionSegment[segments.Count];
            for (var i = 0; i < segments.Count; i++)
            {
                var start = i == 0 ? 0 : map[starts[i]];
                var end = i == segments.Count - 1 ? originalText.Length : map[starts[i + 1]];
                result[i] = segments[i] with { Text = originalText[start..end] };
            }

            aligned = result;
            return true;
        }

        /// <summary>
        /// Depth-first walk: match segment <paramref name="index"/> at <paramref name="pos"/> under
        /// each tolerance candidate, recursing into the rest; backtracks when a candidate strands a
        /// later segment. Branching is tiny (≤12 candidates, most rejected up front).
        /// </summary>
        private static bool Match(string normalized, string[] segmentTexts, int index, int pos, int[] starts)
        {
            if (index == segmentTexts.Length)
                return TailIsIgnorable(normalized, pos);

            var start = SkipWhitespace(normalized, pos);
            var segment = segmentTexts[index];

            foreach (var (skipOriginal, dropLeading, dropTrailing) in Candidates)
            {
                var p = start;
                if (skipOriginal == 1)
                {
                    if (p >= normalized.Length || !IsSkippablePunctuation(normalized[p]))
                        continue;
                    p = SkipWhitespace(normalized, p + 1);
                }

                if (dropLeading + dropTrailing >= segment.Length)
                    continue;
                if (dropLeading == 1 && !IsSkippablePunctuation(segment[0]))
                    continue;
                if (dropTrailing >= 1 && !IsSkippablePunctuation(segment[^1]))
                    continue;
                if (dropTrailing == 2 && !IsSkippablePunctuation(segment[^2]))
                    continue;

                var core = segment[dropLeading..(segment.Length - dropTrailing)].Trim();
                if (core.Length == 0)
                    continue;

                if (p + core.Length > normalized.Length ||
                    string.CompareOrdinal(normalized, p, core, 0, core.Length) != 0)
                    continue;

                starts[index] = p;
                if (Match(normalized, segmentTexts, index + 1, p + core.Length, starts))
                    return true;
            }

            return false;
        }

        /// <summary>After the last segment: whitespace and at most one punctuation character.</summary>
        private static bool TailIsIgnorable(string normalized, int pos)
        {
            var p = SkipWhitespace(normalized, pos);
            if (p < normalized.Length && IsSkippablePunctuation(normalized[p]))
                p = SkipWhitespace(normalized, p + 1);
            return p == normalized.Length;
        }

        private static int SkipWhitespace(string text, int pos)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            return pos;
        }

        /// <summary>Boundary drift only ever absorbs punctuation — never letters or digits.</summary>
        private static bool IsSkippablePunctuation(char c) =>
            !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);
    }
}
