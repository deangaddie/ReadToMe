using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Read2Me.Data.Enums;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// One shared Voice as the <i>differs</i> check compares it (#205 item 5): every field a series
    /// shares, with the reference audio by its content hash rather than its path (each book keeps its
    /// copy under its own Character's folder). Null <see cref="AudioHash"/>: no reference audio.
    /// </summary>
    public sealed record SeriesVoiceState(
        Guid Id,
        string Name,
        string? Description,
        VoiceSource Source,
        string? DesignPrompt,
        string? Transcript,
        string? VoiceDesignSettingsOverrideJson,
        string? TtsSettingsOverrideJson,
        string? AudioHash);

    /// <summary>A book's whole set of a Series character's Voices, reduced to one comparable string.</summary>
    public static class SeriesVoiceVersion
    {
        /// <summary>The same Voices, in any order, give the same version; any change gives another.</summary>
        public static string Of(IEnumerable<SeriesVoiceState> voices)
        {
            var json = JsonSerializer.Serialize(voices.OrderBy(v => v.Id).ToList());
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }

    /// <summary>
    /// Which version of a Series character's Voices is the <b>series copy</b>, and which linked
    /// books <i>differ from series</i> (#222): the version most linked books hold, a tie going to the
    /// version of the lowest Series position; every book holding another version differs.
    /// </summary>
    public static class SeriesCopyChoice
    {
        /// <summary>A linked book and the version of the Voices it holds.</summary>
        public sealed record Book(string Folder, decimal Position, string Version);

        /// <summary>
        /// The series copy, read from <see cref="SeriesCopyFolder"/> (the lowest-positioned book that
        /// holds it), and the folders whose copy differs.
        /// </summary>
        public sealed record Choice(string SeriesCopyFolder, string Version, IReadOnlySet<string> Differs)
        {
            public bool DiffersFrom(string folder) => Differs.Contains(folder);
        }

        /// <param name="books">Every linked book whose Voices could be read; at least one.</param>
        public static Choice Choose(IReadOnlyList<Book> books)
        {
            if (books.Count == 0) throw new ArgumentException("A series character has at least one linked book.", nameof(books));

            var winner = books
                .GroupBy(b => b.Version)
                .Select(g => (Version: g.Key, Count: g.Count(), Lowest: g.MinBy(b => b.Position)!))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Lowest.Position)
                .First();

            var differs = books
                .Where(b => b.Version != winner.Version)
                .Select(b => b.Folder)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new Choice(winner.Lowest.Folder, winner.Version, differs);
        }
    }
}
