using System.Text.RegularExpressions;

namespace Read2Me.Services.BookEdits
{
    /// <summary>Pure string transforms for the deterministic edit-program kinds.</summary>
    public static class DeterministicTransformer
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        /// <summary>Applies a .NET regex replacement ($1 group substitutions supported).
        /// Throws RegexMatchTimeoutException on catastrophic patterns; callers mark the
        /// item Failed.</summary>
        public static string RegexReplace(string oldValue, string pattern, string? replacement)
            => Regex.Replace(oldValue, pattern, replacement ?? string.Empty, RegexOptions.None, RegexTimeout);

        /// <summary>Renders a whole-value template. Tokens: {n} = 1-based ordinal within
        /// the resolved scope, {old} = current value.</summary>
        public static string RenderTemplate(string template, int n, string oldValue)
            => template
                .Replace("{n}", n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{old}", oldValue);

        /// <summary>Re-cases the whole value when <paramref name="pattern"/> is null, otherwise only
        /// the spans it matches. Upper and lower are literal; sentence and title read sentence
        /// starts and neighbouring words from the whole value and keep roman numerals and acronyms
        /// (see CaseChanger). Throws RegexMatchTimeoutException on catastrophic patterns; callers
        /// mark the item Failed.</summary>
        public static string ChangeCase(string value, string? pattern, CaseMode mode)
        {
            var recased = mode switch
            {
                CaseMode.Upper => value.ToUpperInvariant(),
                CaseMode.Lower => value.ToLowerInvariant(),
                CaseMode.Sentence or CaseMode.Title => CaseChanger.Recase(value, mode),
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
            };
            return pattern == null
                ? recased
                : Regex.Replace(value, pattern, m => recased.Substring(m.Index, m.Length), RegexOptions.None, RegexTimeout);
        }
    }
}
