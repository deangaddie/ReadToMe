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
        /// the spans it matches. Upper and lower are literal. Throws NotSupportedException for
        /// sentence and title case (not built yet) and RegexMatchTimeoutException on catastrophic
        /// patterns; callers mark the item Failed.</summary>
        public static string ChangeCase(string value, string? pattern, CaseMode mode)
        {
            Func<string, string> recase = mode switch
            {
                CaseMode.Upper => s => s.ToUpperInvariant(),
                CaseMode.Lower => s => s.ToLowerInvariant(),
                _ => throw new NotSupportedException($"{mode} case is not supported yet."),
            };
            return pattern == null
                ? recase(value)
                : Regex.Replace(value, pattern, m => recase(m.Value), RegexOptions.None, RegexTimeout);
        }
    }
}
