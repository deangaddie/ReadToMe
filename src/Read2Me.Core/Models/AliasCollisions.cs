namespace Read2Me.Core.Models
{
    /// <summary>
    /// One owner of identity strings in a collision check: a discovery row about to be applied, or
    /// a character already on the roster. <see cref="ExistingCharacterId"/> on a row names the roster
    /// character it will resolve onto, so that character is folded into the row rather than counted
    /// as a second owner.
    /// </summary>
    public sealed record AliasClaim(string Name, IReadOnlyList<string> Aliases, Guid? ExistingCharacterId = null);

    /// <summary>A roster character in a collision check: its id, primary name and aliases.</summary>
    public sealed record AliasOwner(Guid Id, string Name, IReadOnlyList<string> Aliases);

    /// <summary>
    /// Finds identity strings — a character's name or one of its aliases — that would end up
    /// claimed by more than one character once the discovery rows are applied.
    /// </summary>
    /// <remarks>
    /// Attribution resolves a speaker string to a character by first match over an alphabetically
    /// ordered roster (<c>CharacterResolver.ResolveOrCreateAsync</c>), so a string owned by two
    /// characters silently binds to whichever sorts first, in every scene. Discovery is where these
    /// arrive: asked for the cast of <i>Pride and Prejudice</i>, the LLM handed <c>Miss Bennet</c> to
    /// all five Bennet daughters. This is advisory — the review surfaces it and the user removes the
    /// offending alias. Nothing here blocks an apply. The web client carries a line-for-line port
    /// (<c>alias-collisions.ts</c>) tested against the same fixtures.
    /// </remarks>
    public static class AliasCollisions
    {
        /// <summary>
        /// Returns the strings owned by two or more characters, comparing case-insensitively.
        /// Pass only the rows that will be applied — an excluded row is never applied.
        /// </summary>
        public static IReadOnlySet<string> Find(IEnumerable<AliasClaim> rows, IEnumerable<AliasOwner> roster)
        {
            var included = rows.ToList();
            var claimed = included
                .Where(r => r.ExistingCharacterId is { } id)
                .Select(r => r.ExistingCharacterId!.Value)
                .ToHashSet();

            var owners = included
                .Select(r => r.Aliases.Prepend(r.Name))
                .Concat(roster
                    .Where(c => !claimed.Contains(c.Id))
                    .Select(c => c.Aliases.Prepend(c.Name)));

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
