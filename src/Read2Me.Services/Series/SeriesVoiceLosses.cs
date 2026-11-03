namespace Read2Me.Services.Series
{
    /// <summary>What one book loses when its Voices of a Series character are replaced (#222): Voice names and the positional rules on them.</summary>
    public sealed record SeriesVoiceLoss(string Folder, string Title, IReadOnlyList<string> Voices, int RuleCount);

    /// <summary>
    /// What replacing a book's Voices with another book's whole set removes (#222): every Voice the
    /// book holds that the set lacks, and the book's positional Voice Rules on them. A push or take
    /// names these in its confirm; an edit made in the series copy doesn't bring back in line a book
    /// that would lose rules, since nothing asks first there.
    /// </summary>
    public static class SeriesVoiceLosses
    {
        /// <param name="kept">The Voice Ids of the set the book takes.</param>
        /// <param name="held">The book's own Voices, by Id with their names.</param>
        /// <param name="positionalRuleVoices">The Voice Id of each of the book's positional rules.</param>
        public static SeriesVoiceLoss? Of(
            string folder, string title,
            IReadOnlyCollection<Guid> kept,
            IReadOnlyList<(Guid Id, string Name)> held,
            IReadOnlyList<Guid> positionalRuleVoices)
        {
            var lost = held.Where(v => !kept.Contains(v.Id)).ToList();
            if (lost.Count == 0) return null;
            var lostIds = lost.Select(v => v.Id).ToHashSet();
            return new SeriesVoiceLoss(folder, title, lost.Select(v => v.Name).ToList(), positionalRuleVoices.Count(lostIds.Contains));
        }
    }
}
