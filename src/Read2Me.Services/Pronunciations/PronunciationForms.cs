using System.Text.RegularExpressions;

namespace Read2Me.Services.Pronunciations;

/// <summary>
/// What a written form and a spoken form may be. Both are trimmed and their inner whitespace runs
/// collapsed to one space before anything checks or stores them.
/// </summary>
public static partial class PronunciationForms
{
    public const int MaxWrittenLength = 100;
    public const int MaxSpokenLength = 200;

    /// <summary>Trimmed, every run of whitespace inside it one space.</summary>
    public static string Normalise(string? form) =>
        string.Join(' ', (form ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Why the normalised written form can't be one, or null when it can.</summary>
    public static string? WrittenProblem(string written)
    {
        if (written.Length == 0)
            return "Written form is required.";
        if (written.Length > MaxWrittenLength)
            return $"Written form can be at most {MaxWrittenLength} characters.";
        if (!WordEdges().IsMatch(written))
            return "Written form must start and end with a letter or digit.";
        return null;
    }

    /// <summary>Why the normalised spoken form can't be one, or null when it can.</summary>
    public static string? SpokenProblem(string spoken)
    {
        if (spoken.Length == 0)
            return "Spoken form is required.";
        if (spoken.Length > MaxSpokenLength)
            return $"Spoken form can be at most {MaxSpokenLength} characters.";
        return null;
    }

    /// <summary>Starts and ends with what the matcher counts as a word character, so a match can find its edges.</summary>
    [GeneratedRegex("^" + PronunciationMatcher.WordCharacter + "(.*" + PronunciationMatcher.WordCharacter + ")?$", RegexOptions.Singleline)]
    private static partial Regex WordEdges();
}
