namespace Read2Me.Services.Series
{
    /// <summary>
    /// Who may link to a Series character (ADR 0018, context/series.md "Linked character"): a book
    /// links at most one Character to a given Series character, and a Character is linked to at most
    /// one Series character. The seed Narrator row is a Character like any other. A Series character
    /// left with no links is removed.
    /// </summary>
    public static class SeriesLinkRules
    {
        /// <summary>One stored link: a book's Character tied to a Series character.</summary>
        public sealed record Link(Guid SeriesCharacterId, Guid ProjectId, Guid CharacterId);

        public abstract record Verdict;

        public sealed record Allowed : Verdict;

        /// <summary>The book already links <see cref="CharacterId"/> to this Series character: merge first.</summary>
        public sealed record BookAlreadyLinked(Guid CharacterId) : Verdict;

        /// <summary>The Character is already linked, to <see cref="SeriesCharacterId"/> (maybe this one).</summary>
        public sealed record CharacterAlreadyLinked(Guid SeriesCharacterId) : Verdict;

        /// <summary>May <paramref name="characterId"/> of <paramref name="projectId"/> link to <paramref name="seriesCharacterId"/>?</summary>
        public static Verdict Check(IEnumerable<Link> links, Guid seriesCharacterId, Guid projectId, Guid characterId)
        {
            var all = links as IReadOnlyCollection<Link> ?? links.ToList();
            if (CheckShare(all, projectId, characterId) is { } linked)
                return linked;
            return all.FirstOrDefault(l => l.SeriesCharacterId == seriesCharacterId && l.ProjectId == projectId) is { } held
                ? new BookAlreadyLinked(held.CharacterId)
                : new Allowed();
        }

        /// <summary>Sharing a Character as a new Series character: refused only when it is already linked.</summary>
        public static CharacterAlreadyLinked? CheckShare(IEnumerable<Link> links, Guid projectId, Guid characterId) =>
            links.FirstOrDefault(l => l.ProjectId == projectId && l.CharacterId == characterId) is { } held
                ? new CharacterAlreadyLinked(held.SeriesCharacterId)
                : null;

        /// <summary>The Series characters whose every link <paramref name="removed"/> takes away.</summary>
        public static IReadOnlyList<Guid> OrphanedBy(IEnumerable<Link> links, Func<Link, bool> removed) =>
            links.GroupBy(l => l.SeriesCharacterId)
                .Where(g => g.All(removed))
                .Select(g => g.Key)
                .ToList();
    }
}
