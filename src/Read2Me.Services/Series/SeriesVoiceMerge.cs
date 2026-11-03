namespace Read2Me.Services.Series
{
    /// <summary>
    /// The Voice merge a link runs (ADR 0018, Character linking #208): the joining Character gains
    /// every Voice the linked books hold that it lacks, and each linked book gains every Voice of the
    /// joining Character it lacks. Voices are matched by Voice Id, the same in every book, so a
    /// Voice both sides hold is never copied (its content is the <i>differs</i> check's business).
    /// </summary>
    public static class SeriesVoiceMerge
    {
        /// <summary>One book's linked Character's Voices; books in Series position order.</summary>
        public sealed record BookVoices<T>(string Folder, IReadOnlyList<T> Voices);

        /// <summary>A Voice the joining Character gains, copied from <see cref="FromFolder"/>.</summary>
        public sealed record Gain<T>(string FromFolder, T Voice);

        /// <summary>
        /// What linking writes: <see cref="JoiningGains"/> into the joining book, and for each linked
        /// book that gains anything, its gains (copied from the joining book), in position order.
        /// </summary>
        public sealed record Plan<T>(IReadOnlyList<Gain<T>> JoiningGains, IReadOnlyList<BookVoices<T>> LinkedGains);

        /// <param name="joining">The joining Character's Voices.</param>
        /// <param name="linked">The books already linked, lowest Series position first.</param>
        /// <param name="idOf">A Voice's Id.</param>
        public static Plan<T> Make<T>(
            IReadOnlyList<T> joining, IReadOnlyList<BookVoices<T>> linked, Func<T, Guid> idOf)
        {
            var held = joining.Select(idOf).ToHashSet();
            var joiningGains = new List<Gain<T>>();
            foreach (var book in linked)
                foreach (var voice in book.Voices)
                    if (held.Add(idOf(voice)))
                        joiningGains.Add(new Gain<T>(book.Folder, voice));

            var linkedGains = new List<BookVoices<T>>();
            foreach (var book in linked)
            {
                var has = book.Voices.Select(idOf).ToHashSet();
                var gains = joining.Where(v => !has.Contains(idOf(v))).ToList();
                if (gains.Count > 0)
                    linkedGains.Add(new BookVoices<T>(book.Folder, gains));
            }
            return new Plan<T>(joiningGains, linkedGains);
        }
    }
}
