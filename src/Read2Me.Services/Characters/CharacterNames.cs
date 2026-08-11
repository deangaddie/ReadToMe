using Read2Me.Data;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Known-character name matching shared by the single-speaker attribution chain and the
    /// per-item attribution path: LLM answers are compared trimmed + OrdinalIgnoreCase, with aliases
    /// resolving to their owner character's name.
    /// <para>
    /// The reserved <see cref="AttributionWire.Narrator"/> token is judged <b>before</b> the roster walk,
    /// because the roster contains the seed <c>Narrator</c> row and would otherwise make the token
    /// quietly "known" whether or not a narrator link is set. Linked, the token is a wire alias of
    /// the linked character (reader-side tolerance only — it is never advertised in the roster);
    /// unlinked, it resolves to nothing, since a narrator who is not a character cannot speak in
    /// scene.
    /// </para>
    /// </summary>
    internal static class CharacterNames
    {
        /// <summary>
        /// True when <paramref name="name"/> matches a known character name or any alias
        /// (case-insensitive, trimmed). A resolved answer outside this set is an unlisted name.
        /// </summary>
        public static bool IsKnown(
            string name, IReadOnlyList<Data.Entities.Character> characters, NarratorIdentity narrator) =>
            AttributionWire.IsNarrator(name)
                ? narrator.IsLinked
                : FindOwner(name.Trim(), characters) != null;

        /// <summary>
        /// Maps an alias to its owner character's name; returns other names trimmed; null → null.
        /// The narrator token canonicalizes to the linked character's name; unlinked it owns nobody
        /// and stays itself.
        /// </summary>
        public static string? Canonicalize(
            string? name, IReadOnlyList<Data.Entities.Character> characters, NarratorIdentity narrator)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();
            if (AttributionWire.IsNarrator(trimmed))
                // Unlinked the token owns nobody, so it canonicalizes to itself — deliberately not
                // null, which is what a blank speaker yields: two samples answering "narrator" and
                // "" would then compare equal, and today they do not.
                return narrator.IsLinked ? narrator.DisplayName.Trim() : trimmed;
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
