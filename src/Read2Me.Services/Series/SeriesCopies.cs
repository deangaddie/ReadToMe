namespace Read2Me.Services.Series
{
    /// <summary>One other linked book of a Series character: where it is and which of its Characters is linked.</summary>
    public sealed record SeriesPeer(string Folder, string Title, decimal Position, Guid CharacterId)
    {
        public Core.Models.ProjectFolderId FolderId => new(Folder);
    }

    /// <summary>A book's series-linked Character: its series and Series character, the book itself, and the other linked books.</summary>
    /// <param name="Self">The book itself, as its peers see it.</param>
    public sealed record SeriesPeers(Guid SeriesId, Guid SeriesCharacterId, SeriesPeer Self, IReadOnlyList<SeriesPeer> Others);

    /// <summary>How a copy into one book went: <see cref="Failure"/> is null when it committed; <see cref="Rejection"/> says what kind of failure it was.</summary>
    public sealed record SeriesCopyResult(string Folder, string? Failure)
    {
        public Mutations.BookMutationRejection? Rejection { get; init; }
    }

    /// <summary>
    /// The write order of a copy across books (#205 item 6): after the book the edit was made in,
    /// each other linked book in Series position order. <see cref="SeriesVoiceReplicator"/> writes
    /// them in that order; a book whose copy fails is recorded and the rest carry on, and nothing
    /// is rolled back across books.
    /// </summary>
    public static class SeriesCopies
    {
        /// <param name="others">The other linked books, in any order.</param>
        public static IReadOnlyList<SeriesPeer> InWriteOrder(IEnumerable<SeriesPeer> others) =>
            others.OrderBy(b => b.Position).ToList();
    }
}
