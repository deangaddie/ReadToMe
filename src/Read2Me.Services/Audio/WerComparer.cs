using System;
using System.Collections.Generic;
using System.Text;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Pure Word Error Rate comparer. Both strings are normalized (lowercased, punctuation
    /// stripped, spelled-out numbers canonicalized to digits) before a token-level Levenshtein
    /// distance is divided by the reference token count.
    /// </summary>
    public class WerComparer : IWerComparer
    {
        public double Compute(string reference, string hypothesis)
        {
            var refTokens = Normalize(reference);
            var hypTokens = Normalize(hypothesis);

            if (refTokens.Count == 0)
                return hypTokens.Count == 0 ? 0.0 : 1.0;

            var distance = Levenshtein(refTokens, hypTokens);
            return (double)distance / refTokens.Count;
        }

        /// <summary>
        /// Normalizes raw text into comparison tokens: lowercase invariant, punctuation/symbols
        /// collapsed to spaces (letters and digits kept), spelled-out numbers canonicalized to
        /// digits, whitespace collapsed.
        /// </summary>
        internal static List<string> Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
                else
                    sb.Append(' ');
            }

            var rawTokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return CanonicalizeNumbers(rawTokens);
        }

        /// <summary>
        /// Walks tokens left-to-right, folding runs of number words (0–9999) into digit tokens.
        /// Unknown tokens pass through literally.
        /// </summary>
        internal static List<string> CanonicalizeNumbers(IReadOnlyList<string> tokens)
        {
            var result = new List<string>(tokens.Count);
            long accumulator = 0;     // running total for the current number run
            long current = 0;         // sub-total below the current scale (hundreds group)
            bool inNumber = false;

            void Flush()
            {
                if (inNumber)
                {
                    result.Add((accumulator + current).ToString());
                    accumulator = 0;
                    current = 0;
                    inNumber = false;
                }
            }

            foreach (var token in tokens)
            {
                if (Ones.TryGetValue(token, out var ones))
                {
                    inNumber = true;
                    current += ones;
                }
                else if (Tens.TryGetValue(token, out var tens))
                {
                    inNumber = true;
                    current += tens;
                }
                else if (token == "hundred")
                {
                    inNumber = true;
                    current = (current == 0 ? 1 : current) * 100;
                }
                else if (token == "thousand")
                {
                    inNumber = true;
                    accumulator += (current == 0 ? 1 : current) * 1000;
                    current = 0;
                }
                else if (token == "and" && inNumber)
                {
                    // "one hundred and five" — bridge word inside a number run, ignored.
                }
                else
                {
                    Flush();
                    result.Add(token);
                }
            }

            Flush();
            return result;
        }

        private static int Levenshtein(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var prev = new int[b.Count + 1];
            var curr = new int[b.Count + 1];

            for (var j = 0; j <= b.Count; j++)
                prev[j] = j;

            for (var i = 1; i <= a.Count; i++)
            {
                curr[0] = i;
                for (var j = 1; j <= b.Count; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(prev[j] + 1, curr[j - 1] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }

            return prev[b.Count];
        }

        private static readonly Dictionary<string, long> Ones = new()
        {
            ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
            ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
            ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
            ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
            ["eighteen"] = 18, ["nineteen"] = 19,
        };

        private static readonly Dictionary<string, long> Tens = new()
        {
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
            ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        };
    }
}
