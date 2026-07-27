using Read2Me.Data.Entities;

namespace Read2Me.App.State
{
    /// <summary>
    /// Finds identity strings — a character's name or one of its aliases — that would end up
    /// claimed by more than one character once the discovery rows are applied.
    /// </summary>
    /// <remarks>
    /// Attribution resolves a speaker string to a character by first match over an alphabetically
    /// ordered roster (<c>CharacterResolver.ResolveOrCreateAsync</c>), so a string owned by two
    /// characters silently binds to whichever sorts first, in every scene. Discovery is where these
    /// arrive: asked for the cast of <i>Pride and Prejudice</i>, the LLM handed <c>Miss Bennet</c> to
    /// all five Bennet daughters. This is advisory — the review dialog surfaces it and the user
    /// removes the offending alias. Nothing here blocks an apply.
    /// </remarks>
    public static class AliasCollisions
    {
        /// <summary>
        /// Returns the strings owned by two or more characters, comparing case-insensitively.
        /// Only included rows count — an excluded row is never applied. A roster character that an
        /// included row resolves onto is folded into that row rather than counted as a second owner.
        /// </summary>
        public static IReadOnlySet<string> Find(
            IEnumerable<DiscoveredCharacterRow> rows, IEnumerable<Character> roster)
        {
            var included = rows.Where(r => r.Included).ToList();
            var claimed = included
                .Where(r => r.ExistingCharacterId is { } id)
                .Select(r => r.ExistingCharacterId!.Value)
                .ToHashSet();

            var owners = included
                .Select(r => r.Aliases.Prepend(r.Name))
                .Concat(roster
                    .Where(c => !claimed.Contains(c.Id))
                    .Select(c => c.Aliases.Select(a => a.Name).Prepend(c.Name)));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in owners)
            {
                // Distinct within an owner: one character listing the same alias twice is untidy,
                // not ambiguous.
                foreach (var name in owner
                             .Where(n => !string.IsNullOrWhiteSpace(n))
                             .Select(n => n.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!seen.Add(name))
                        collisions.Add(name);
                }
            }
            return collisions;
        }
    }
}
