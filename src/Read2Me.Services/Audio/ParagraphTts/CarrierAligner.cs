using Read2Me.Services.Audio.Transcription;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Locates the boundary between carrier text and target speech inside a word-timestamped
    /// transcription of carrier+target audio. Both sides are normalized with the same rules as
    /// WER scoring (lowercase, punctuation stripped, numbers canonicalized), then the split
    /// point is searched near the carrier token count and accepted only when the carrier
    /// prefix matches with high confidence. Returns null when no confident boundary exists.
    /// </summary>
    public static class CarrierAligner
    {
        /// <summary>How far the split index may drift from the carrier token count.</summary>
        private const int SplitSearchRadius = 3;

        /// <summary>Maximum Levenshtein distance over carrier token count to accept a split.</summary>
        private const double MaxDistanceRatio = 0.4;

        /// <summary>
        /// Finds where carrier speech ends and target speech begins. CarrierEnd is the end
        /// timestamp of the last carrier word, TargetStart the start timestamp of the first
        /// target word; the caller cuts somewhere inside that gap.
        /// </summary>
        public static (double CarrierEnd, double TargetStart)? FindBoundary(
            string carrierText, IReadOnlyList<TranscribedWord> words)
        {
            var carrierTokens = WerComparer.Normalize(carrierText);
            if (carrierTokens.Count == 0 || words.Count == 0)
                return null;

            // Flatten transcribed words into normalized tokens, remembering which word each
            // token came from (a whisper word may normalize to zero or several tokens).
            var tokens = new List<string>();
            var tokenWordIndex = new List<int>();
            for (var w = 0; w < words.Count; w++)
            {
                foreach (var token in WerComparer.Normalize(words[w].Word))
                {
                    tokens.Add(token);
                    tokenWordIndex.Add(w);
                }
            }

            if (tokens.Count < 2)
                return null; // need at least one carrier token and one target token

            var prefixDistances = LevenshteinPrefixDistances(carrierTokens, tokens);

            // Search the split index near the carrier token count; k tokens go to the carrier,
            // the rest to the target. At least one token must remain on each side.
            var n = carrierTokens.Count;
            var bestK = -1;
            var bestDistance = int.MaxValue;
            var lo = Math.Max(1, n - SplitSearchRadius);
            var hi = Math.Min(tokens.Count - 1, n + SplitSearchRadius);
            for (var k = lo; k <= hi; k++)
            {
                var distance = prefixDistances[k];
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestK = k;
                }
            }

            if (bestK < 0 || (double)bestDistance / n > MaxDistanceRatio)
                return null;

            var lastCarrierWord = tokenWordIndex[bestK - 1];
            var firstTargetWord = tokenWordIndex[bestK];
            if (lastCarrierWord == firstTargetWord)
                return null; // boundary falls inside one transcribed word — no gap to cut in

            return (words[lastCarrierWord].End, words[firstTargetWord].Start);
        }

        /// <summary>
        /// One DP pass over the full token list: result[j] = Levenshtein(reference, tokens[..j]).
        /// </summary>
        private static int[] LevenshteinPrefixDistances(
            IReadOnlyList<string> reference, IReadOnlyList<string> tokens)
        {
            var prev = new int[tokens.Count + 1];
            var curr = new int[tokens.Count + 1];

            for (var j = 0; j <= tokens.Count; j++)
                prev[j] = j;

            for (var i = 1; i <= reference.Count; i++)
            {
                curr[0] = i;
                for (var j = 1; j <= tokens.Count; j++)
                {
                    var cost = reference[i - 1] == tokens[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(prev[j] + 1, curr[j - 1] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }

            return prev;
        }
    }
}
