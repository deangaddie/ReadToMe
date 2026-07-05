namespace Read2Me.Services.Llm
{
    /// <summary>
    /// One voice in the LLM's voice plan for a character.
    /// The UI shows this shape read-only. Keep in sync with JsonExample below.
    /// </summary>
    public sealed record VoicePlanVoice(
        string Name,
        string? Description,
        string DesignPrompt);

    public static class VoicePlanSchema
    {
        /// <summary>
        /// Injected into the prompt via {{response_format}} and shown read-only in the UI.
        /// </summary>
        public const string JsonExample =
            "[ { \"name\": \"Young Pip\", \"description\": \"Boyish voice used from Part 1, Chapter 1 to Part 1, Chapter 7\", \"design_prompt\": \"A young English boy's voice, light and earnest...\" } ]";

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
                  "name": { "type": "string" },
                  "description": { "type": "string" },
                  "design_prompt": { "type": "string" }
                },
                "required": ["name", "description", "design_prompt"]
              }
            }
            """;
    }
}
