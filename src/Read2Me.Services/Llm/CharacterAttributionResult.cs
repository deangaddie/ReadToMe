namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Expected JSON response from the book-character prompt.
    /// The UI shows this shape read-only. Keep in sync with JsonExample below.
    /// </summary>
    public sealed record CharacterAttributionResult(
        string Character,
        string VoiceInstructions);

    public static class CharacterAttributionSchema
    {
        /// <summary>
        /// Injected into the prompt via {{response_format}} and shown read-only in the UI.
        /// </summary>
        public const string JsonExample =
            "{ \"character\": \"Narrator\", \"voice_instructions\": \"calm, measured\" }";
    }
}
