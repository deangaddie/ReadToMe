using System.Text;
using System.Text.RegularExpressions;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Pronunciations;

/// <summary>Where one written form matched: a span of the original text and the entry it names.</summary>
public sealed record PronunciationMatch(int Start, int Length, Guid PronunciationId);

/// <summary>An item's <b>Spoken text</b> and the spans of its text the Pronunciation list matched.</summary>
public sealed record SpokenText(string Text, IReadOnlyList<PronunciationMatch> Matches);

/// <summary>
/// Applies a book's Pronunciation list to text: the one place its rules live, so the TTS request,
/// verify, the Book view's marks and the counts never disagree.
/// <list type="bullet">
/// <item>A written form matches whole words, ignoring case: the characters either side are not letters
/// or digits. A space in it matches any run of whitespace.</item>
/// <item>One left-to-right pass; at each position the longest written form wins, matches never overlap
/// and a spoken form is never rescanned.</item>
/// <item>The match's case carries over: all upper (two letters or more) upper-cases the spoken form;
/// otherwise only its first letter follows the match's, so stress capitals ("Her-MY-oh-nee") survive.</item>
/// </list>
/// Build one per list and reuse it across items: the pattern is compiled once.
/// </summary>
public sealed class PronunciationMatcher
{
    /// <summary>What words are made of: a match's neighbours are never one, and a written form starts and ends with one.</summary>
    internal const string WordCharacter = @"[\p{L}\p{N}]";

    private readonly Regex? _pattern;
    private readonly IReadOnlyList<Pronunciation> _entries;

    public PronunciationMatcher(IEnumerable<Pronunciation> list)
    {
        // Longest first: the alternation tries them in order, and backtracks to a shorter one only
        // when a longer one fails its word edge.
        _entries = list
            .Where(p => !string.IsNullOrWhiteSpace(p.WrittenForm))
            .OrderByDescending(p => string.Join(' ', WordsOf(p.WrittenForm)).Length)
            .ToList();
        if (_entries.Count == 0)
            return;

        var alternatives = _entries.Select((p, i) =>
            $"(?<p{i}>{string.Join(@"\s+", WordsOf(p.WrittenForm).Select(Regex.Escape))})");
        _pattern = new Regex(
            $"(?<!{WordCharacter})(?:{string.Join('|', alternatives)})(?!{WordCharacter})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
    }

    /// <summary>The text with the list applied, and where it matched, from one pass.</summary>
    public SpokenText Apply(string text)
    {
        if (_pattern is null || text.Length == 0)
            return new SpokenText(text, []);

        var matches = new List<PronunciationMatch>();
        var spoken = new StringBuilder(text.Length);
        var at = 0;
        foreach (Match match in _pattern.Matches(text))
        {
            var entry = EntryOf(match);
            matches.Add(new PronunciationMatch(match.Index, match.Length, entry.Id));
            spoken.Append(text, at, match.Index - at).Append(CarryCase(match.Value, entry.SpokenForm));
            at = match.Index + match.Length;
        }
        spoken.Append(text, at, text.Length - at);
        return new SpokenText(matches.Count == 0 ? text : spoken.ToString(), matches);
    }

    /// <summary>The item's Spoken text: what the TTS provider is asked to say.</summary>
    public string SpokenTextOf(string text) => Apply(text).Text;

    /// <summary>The spans of <paramref name="text"/> the list matches.</summary>
    public IReadOnlyList<PronunciationMatch> MatchesIn(string text) => Apply(text).Matches;

    private Pronunciation EntryOf(Match match)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (match.Groups[$"p{i}"].Success)
                return _entries[i];
        throw new InvalidOperationException("A match named no Pronunciation.");
    }

    private static string CarryCase(string matched, string spokenForm)
    {
        var letters = matched.Where(char.IsLetter).ToList();
        if (letters.Count == 0)
            return spokenForm;
        if (letters.Count >= 2 && letters.All(char.IsUpper))
            return spokenForm.ToUpperInvariant();

        var first = 0;
        while (first < spokenForm.Length && !char.IsLetter(spokenForm[first]))
            first++;
        if (first == spokenForm.Length)
            return spokenForm;
        var cased = char.IsUpper(letters[0])
            ? char.ToUpperInvariant(spokenForm[first])
            : char.ToLowerInvariant(spokenForm[first]);
        return string.Concat(spokenForm.AsSpan(0, first), cased.ToString(), spokenForm.AsSpan(first + 1));
    }

    private static string[] WordsOf(string writtenForm) =>
        writtenForm.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
