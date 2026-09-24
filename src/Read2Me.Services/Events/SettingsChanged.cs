namespace Read2Me.Services.Events;

/// <summary>
/// A settings area was written (create / update / delete / active selection / prompt / theme).
/// Published on the singleton <c>EventBroadcaster&lt;SettingsChanged&gt;</c> by every settings
/// service alongside its own scoped <c>OnChanged</c>, so a process-wide listener (the live hub
/// relay) can tell other clients to refresh. Carries only the area: a client rereads the list.
/// </summary>
public sealed record SettingsChanged(string Area);

/// <summary>Area names on the wire; the generic settings routes use the same segments.</summary>
public static class SettingsArea
{
    public const string Llm = "llm";
    public const string ParagraphTts = "paragraph-tts";
    public const string VoiceDesign = "voice-design";
    public const string Transcription = "transcription";
    public const string SemanticSimilarity = "semantic-similarity";
    public const string AudioProcessing = "audio-processing";
    public const string Prompts = "prompts";
    public const string Themes = "themes";
}
