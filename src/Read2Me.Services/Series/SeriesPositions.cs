namespace Read2Me.Services.Series
{
    /// <summary>
    /// The Series position rules (#206, <c>context/series.md</c>). A position is a number the user
    /// types: 0 or more, at most two decimal places. No two books in a series share one; gaps are
    /// fine. A joining book gets the next whole number after the highest, and nothing is ever
    /// renumbered. A series' positions are saved as one set, checked together, so a swap never
    /// meets a tie halfway through.
    /// </summary>
    public static class SeriesPositions
    {
        public static bool IsWellFormed(decimal position) =>
            position >= 0 && decimal.Round(position, 2) == position;

        /// <summary>The position a book joining a series with <paramref name="taken"/> gets: 2.5 → 3, none → 1.</summary>
        public static decimal Next(IEnumerable<decimal> taken)
        {
            var highest = taken.DefaultIfEmpty(0m).Max();
            return decimal.Floor(highest) + 1;
        }

        /// <summary>The same number without trailing zeros (2.50 → 2.5), as it is shown and sent.</summary>
        public static decimal Normalize(decimal position) => position / 1.0000000000000000000000000000m;

        /// <summary>
        /// Checks a whole set of positions, keyed by book, against the series' members: every
        /// position well formed, the keys exactly the members (ignoring case, as folders do), and no
        /// two books on one number. A malformed position is a malformed request and is reported first.
        /// </summary>
        public static SeriesPositionCheck CheckSet(
            IReadOnlyCollection<string> members, IReadOnlyDictionary<string, decimal> positions)
        {
            foreach (var (book, position) in positions)
                if (!IsWellFormed(position))
                    return new SeriesPositionCheck.Malformed(book, position);

            var memberSet = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
            var keySet = new HashSet<string>(positions.Keys, StringComparer.OrdinalIgnoreCase);
            if (!memberSet.SetEquals(keySet) || keySet.Count != positions.Count)
                return new SeriesPositionCheck.OutOfDate();

            var holder = new Dictionary<decimal, string>();
            foreach (var (book, position) in positions)
            {
                if (holder.TryGetValue(position, out var other))
                    return new SeriesPositionCheck.Tie(other, book, Normalize(position));
                holder[position] = book;
            }
            return new SeriesPositionCheck.Accepted();
        }
    }

    /// <summary>What <see cref="SeriesPositions.CheckSet"/> found.</summary>
    public abstract record SeriesPositionCheck
    {
        public sealed record Accepted : SeriesPositionCheck;

        /// <summary>Under 0, or more than two decimal places.</summary>
        public sealed record Malformed(string Book, decimal Position) : SeriesPositionCheck;

        /// <summary>The set's books are not exactly the series' members: someone joined or left since it was read.</summary>
        public sealed record OutOfDate : SeriesPositionCheck;

        /// <summary>Two books on one number.</summary>
        public sealed record Tie(string First, string Second, decimal Position) : SeriesPositionCheck;
    }
}
