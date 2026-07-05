namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Expected JSON response from the book-character prompt.
    /// The UI shows this shape read-only. Keep in sync with JsonExample below.
    /// </summary>
    public sealed record CharacterAttributionResult(
        string Character,
        string VoiceInstructions,
        string? Reasoning = null);

    public static class CharacterAttributionSchema
    {
        /// <summary>
        /// Injected into the prompt via {{response_format}} and shown read-only in the UI.
        /// </summary>
        public const string JsonExample =
            "{ \"reasoning\": \"brief note on how you identified the speaker\", \"character\": \"Narrator\", \"voice_instructions\": \"calm, measured\" }";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Property order matters: reasoning first so the model reasons before answering.
        /// Keep in sync with JsonExample above.
        /// </summary>
        public const string JsonSchema = """
            {
              "type": "object",
              "properties": {
                "reasoning": { "type": "string" },
                "character": { "type": "string" },
                "voice_instructions": { "type": "string" }
              },
              "required": ["reasoning", "character", "voice_instructions"]
            }
            """;
    }

    public static class CharacterBatchAttributionSchema
    {
        /// <summary>
        /// Injected into the batch prompt via {{response_format}} and shown read-only in the UI.
        /// </summary>
        public const string JsonExample =
            "[ { \"index\": 0, \"reasoning\": \"brief note on how you identified the speaker\", \"character\": \"Narrator\", \"voice_instructions\": \"calm, measured\" } ]";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Keep in sync with JsonExample above.
        /// </summary>
        public const string JsonSchema = """
            {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "index": { "type": "integer" },
                  "reasoning": { "type": "string" },
                  "character": { "type": "string" },
                  "voice_instructions": { "type": "string" }
                },
                "required": ["index", "reasoning", "character", "voice_instructions"]
              }
            }
            """;
    }
}
