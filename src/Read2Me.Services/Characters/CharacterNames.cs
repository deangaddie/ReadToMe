namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Known-character name matching shared by the single-speaker attribution chain and the
    /// segment-list path: LLM answers are compared trimmed + OrdinalIgnoreCase, with aliases
    /// resolving to their owner character's name.
    /// </summary>
    internal static class CharacterNames
    {
        /// <summary>
        /// True when <paramref name="name"/> matches a known character name or any alias
        /// (case-insensitive, trimmed). A resolved answer outside this set is an unlisted name.
        /// </summary>
        public static bool IsKnown(string name, IReadOnlyList<Data.Entities.Character> characters) =>
            FindOwner(name.Trim(), characters) != null;

        /// <summary>Maps an alias to its owner character's name; returns other names trimmed; null → null.</summary>
        public static string? Canonicalize(string? name, IReadOnlyList<Data.Entities.Character> characters)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();
            return FindOwner(trimmed, characters)?.Name.Trim() ?? trimmed;
        }

        /// <summary>The character whose name or alias matches (trimmed, OrdinalIgnoreCase), or null.</summary>
        private static Data.Entities.Character? FindOwner(
            string trimmed, IReadOnlyList<Data.Entities.Character> characters)
        {
            foreach (var c in characters)
            {
                if (c.Name.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return c;
                foreach (var alias in c.Aliases)
                    if (alias.Name.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }
    }
}
