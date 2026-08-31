namespace Read2Me.Services.Llm;

/// <summary>
/// Runtime text for narrator-link prompt tokens. Templates own placement; this type owns the
/// measured wording and its newline framing so sent prompts and UI previews cannot drift.
/// </summary>
public static class NarratorPromptText
{
    public static string IdentityParagraph(string displayName) =>
        $"\nThis book is narrated by {displayName}, who is also a character in the story and speaks in scene.\n";

    public const string AlsoNarratesParagraph =
        "\nThis character also narrates the entire book — the same voice reads all the prose, not only this character's dialogue. Choose a clear, even delivery that can sustain hours of narration.\n";
}
