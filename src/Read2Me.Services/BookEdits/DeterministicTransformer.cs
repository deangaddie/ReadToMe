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
    }
}
