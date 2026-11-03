namespace Read2Me.Services.Series
{
    /// <summary>One other linked book of a Series character: where it is and which of its Characters is linked.</summary>
    public sealed record SeriesPeer(string Folder, string Title, decimal Position, Guid CharacterId)
    {
        public Core.Models.ProjectFolderId FolderId => new(Folder);
    }

    /// <summary>A book's series-linked Character: its series and Series character, and the other linked books.</summary>
    public sealed record SeriesPeers(Guid SeriesId, Guid SeriesCharacterId, IReadOnlyList<SeriesPeer> Others);

    /// <summary>How a copy into one book went: <see cref="Failure"/> is null when it committed.</summary>
    public sealed record SeriesCopyResult(string Folder, string? Failure);

    /// <summary>
    /// The write order of a copy across books (#205 item 6): after the book the edit was made in,
    /// each other linked book in Series position order. A book whose copy fails is recorded and the
    /// rest carry on; nothing is rolled back across books.
    /// </summary>
    public static class SeriesCopies
    {
        /// <param name="others">The other linked books, in any order.</param>
        /// <param name="copy">Copies into one book; answers why it failed, or null. A throw is a failure too.</param>
        public static async Task<IReadOnlyList<SeriesCopyResult>> RunAsync(
            IEnumerable<SeriesPeer> others, Func<SeriesPeer, Task<string?>> copy)
        {
            var results = new List<SeriesCopyResult>();
            foreach (var book in others.OrderBy(b => b.Position))
            {
                string? failure;
                try
                {
                    failure = await copy(book);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex.Message;
                }
                results.Add(new SeriesCopyResult(book.Folder, failure));
            }
            return results;
        }
    }
}
