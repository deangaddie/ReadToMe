using System.Text.RegularExpressions;

namespace Read2Me.Core.Models
{
    public static class NameSanitizer
    {
        /// <summary>
        /// Produces a filesystem-safe lowercase slug from a display name.
        /// Spaces become hyphens; non-word, non-hyphen characters are stripped;
        /// consecutive hyphens collapse; leading/trailing hyphens are trimmed.
        /// </summary>
        public static string Sanitize(string name)
        {
            var s = name.ToLowerInvariant().Replace(' ', '-');
            s = Regex.Replace(s, @"[^\w\-]", "");
            s = Regex.Replace(s, @"-{2,}", "-").Trim('-');
            return s;
        }
    }
}
