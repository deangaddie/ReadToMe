using System.Text.RegularExpressions;

namespace Read2Me.Services.Characters;

/// <summary>
/// The speech-marker classifier behind a Chapter or Part Release narration (ADR 0015): text in, the
/// paragraph's marker key out. Three shapes, tried in order, first match wins:
/// <list type="bullet">
/// <item><b>Initial prefix</b> — one or two capitals, a period or colon, whitespace and more text
/// (<c>Q. Who are you?</c>, <c>A: Fitzwilliam Darcy.</c>), keyed as written (<c>Q.</c> and <c>Q:</c> are
/// different keys).</item>
/// <item><b>Name prefix</b> — one to four capitalised words, a colon and whitespace, where the words
/// are on the book's roster (<c>Darcy:</c>), keyed as written. The roster gate keeps labels
/// such as <c>Later: the Count came back.</c> out.</item>
/// <item><b>Em-dash speech</b> — <c>—</c> and a capital, up to 600 non-dash characters, <c>—</c>,
/// where the opening dash follows the paragraph start, whitespace, a letter or a comma
/// (<c>—What is the law?— the voice asked.</c>). The leading condition keeps a diary header such as
/// <c>7 September.—</c> out. Every match shares the key <see cref="EmDashKey"/>.</item>
/// </list>
/// A key is only a candidate: <see cref="NarrationRelease"/> releases a paragraph only when its key
/// repeats in at least <see cref="MinRepetition"/> narration-only paragraphs of its chapter. The
/// thresholds are fixed, as measured on the released-narration corpus; there is no setting.
/// </summary>
public static partial class SpeechMarkers
{
    /// <summary>How many narration-only paragraphs of one chapter must carry a key before it releases any.</summary>
    public const int MinRepetition = 5;

    /// <summary>The single key every em-dash speech paragraph shares.</summary>
    public const string EmDashKey = "—…—";

    /// <summary>
    /// The roster a name prefix must be on: every Character name and alias of the book, compared
    /// case-insensitively.
    /// </summary>
    public static IReadOnlySet<string> Roster(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The paragraph's speech-marker key, or null when it carries none. <paramref name="text"/> is the
    /// paragraph's speech items' texts in item order, joined by one space.
    /// </summary>
    public static string? KeyOf(string text, IReadOnlySet<string> roster)
    {
        var initial = InitialPrefix().Match(text);
        if (initial.Success) return initial.Groups["key"].Value;

        var name = NamePrefix().Match(text);
        if (name.Success && roster.Contains(name.Groups["name"].Value)) return name.Groups["key"].Value;

        return EmDashSpeech().IsMatch(text) ? EmDashKey : null;
    }

    // 1–2 capitals for an initial.
    [GeneratedRegex(@"^\s*(?<key>[A-Z]{1,2}[.:])\s+\S")]
    private static partial Regex InitialPrefix();

    // 1–4 capitalised words for a name.
    [GeneratedRegex(@"^\s*(?<key>(?<name>[A-Z][A-Za-z’'-]*(?: [A-Z][A-Za-z’'.-]*){0,3}):)\s+\S")]
    private static partial Regex NamePrefix();

    // A capital and up to 600 more non-dash characters inside the dashes.
    [GeneratedRegex(@"(?:^|[\s\p{L},])—[A-Z][^—]{0,600}—")]
    private static partial Regex EmDashSpeech();
}
