namespace Read2Me.Services.Llm
{
    public enum AttributionSegmentType { Narration, Dialog }

    /// <summary>
    /// One segment of a re-segmented paragraph as answered by the LLM. Narration segments carry
    /// the fixed speaker "narrator"; an unresolved dialog speaker is the wire sentinel "unknown".
    /// </summary>
    public sealed record AttributionSegment(
        string Text,
        AttributionSegmentType Type,
        string Speaker,
        string VoiceInstructions);

    /// <summary>
    /// Expected JSON response from the segment-attribution prompt: paragraph-level reasoning
    /// followed by the paragraph's full segment list.
    /// </summary>
    public sealed record SegmentAttributionResult(
        string Reasoning,
        IReadOnlyList<AttributionSegment> Segments);

    public static class SegmentAttributionSchema
    {
        /// <summary>
        /// Injected into the prompt via {{response_format}} and shown read-only in the UI.
        /// Keep in sync with JsonSchema below.
        /// </summary>
        public const string JsonExample =
            "{ \"reasoning\": \"brief note on how you segmented and attributed\", \"segments\": [ " +
            "{ \"text\": \"\\\"Hello,\\\" \", \"type\": \"dialog\", \"speaker\": \"Alice\", \"voice_instructions\": \"warm\" }, " +
            "{ \"text\": \"she said.\", \"type\": \"narration\", \"speaker\": \"narrator\", \"voice_instructions\": \"\" } ] }";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Property order deliberate: reasoning before segments (reason before answering), text
        /// before speaker (commit text, then attribute). minItems 1 outlaws empty lists; no maxItems.
        /// Keep in sync with JsonExample above.
        /// </summary>
        public const string JsonSchema = """
            {
              "type": "object",
              "properties": {
                "reasoning": { "type": "string" },
                "segments": {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "properties": {
                      "text": { "type": "string" },
                      "type": { "type": "string", "enum": ["narration", "dialog"] },
                      "speaker": { "type": "string" },
                      "voice_instructions": { "type": "string" }
                    },
                    "required": ["text", "type", "speaker", "voice_instructions"]
                  }
                }
              },
              "required": ["reasoning", "segments"]
            }
            """;
    }

    public static class SegmentBatchAttributionSchema
    {
        /// <summary>
        /// Injected into the batch prompt via {{response_format}} and shown read-only in the UI.
        /// Keep in sync with JsonSchema below.
        /// </summary>
        public const string JsonExample =
            "[ { \"index\": 0, \"reasoning\": \"brief note on how you segmented and attributed\", \"segments\": [ " +
            "{ \"text\": \"\\\"Hello,\\\" \", \"type\": \"dialog\", \"speaker\": \"Alice\", \"voice_instructions\": \"warm\" }, " +
            "{ \"text\": \"she said.\", \"type\": \"narration\", \"speaker\": \"narrator\", \"voice_instructions\": \"\" } ] } ]";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Wraps the single-response entry in an array with a paragraph index.
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
                  "segments": {
                    "type": "array",
                    "minItems": 1,
                    "items": {
                      "type": "object",
                      "properties": {
                        "text": { "type": "string" },
                        "type": { "type": "string", "enum": ["narration", "dialog"] },
                        "speaker": { "type": "string" },
                        "voice_instructions": { "type": "string" }
                      },
                      "required": ["text", "type", "speaker", "voice_instructions"]
                    }
                  }
                },
                "required": ["index", "reasoning", "segments"]
              }
            }
            """;
    }
}
