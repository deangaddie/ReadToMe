namespace Read2Me.Services.Llm
{
    /// <summary>
    /// One answered ParagraphItem: the index the prompt gave it (0..n-1 in Order sequence within its
    /// paragraph) and who speaks it. There is no text and no type — item boundaries are frozen, so
    /// the model names an item that already exists rather than describing one (see ADR 0005).
    /// An unresolved speaker is the wire sentinel "unknown".
    /// <para>
    /// <see cref="VoiceInstructions"/> is nullable because the answer is the whole truth for an item
    /// it names: absent instructions overwrite to null rather than to empty (spec §1), so the null
    /// has to survive the parser to reach the apply.
    /// </para>
    /// </summary>
    public sealed record AttributedItem(
        int Index,
        string Speaker,
        string? VoiceInstructions);

    /// <summary>
    /// Expected JSON response from the item-attribution prompt: paragraph-level reasoning followed
    /// by the items the model chose to answer. Answering no items is valid — those items stay
    /// unattributed and escalate.
    /// </summary>
    public sealed record ItemAttributionResult(
        string Reasoning,
        IReadOnlyList<AttributedItem> Items);

    public static class ItemAttributionSchema
    {
        /// <summary>
        /// Injected into the prompt via {{response_format}} and shown read-only in the UI.
        /// Keep in sync with JsonSchema below.
        /// </summary>
        public const string JsonExample =
            "{ \"reasoning\": \"brief note on the attribution tag(s) you found, or that there are none\", \"items\": [ " +
            "{ \"index\": 0, \"speaker\": \"Alice\", \"voice_instructions\": \"warm\" }, " +
            "{ \"index\": 2, \"speaker\": \"unknown\", \"voice_instructions\": \"\" } ] }";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Property order deliberate: reasoning before items (reason before answering), index before
        /// speaker (name the item, then attribute it). No minItems — an empty list is a valid
        /// answer, and forcing one would make the model invent an attribution.
        /// Keep in sync with JsonExample above.
        /// </summary>
        public const string JsonSchema = """
            {
              "type": "object",
              "properties": {
                "reasoning": { "type": "string" },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "index": { "type": "integer" },
                      "speaker": { "type": "string" },
                      "voice_instructions": { "type": "string" }
                    },
                    "required": ["index", "speaker", "voice_instructions"]
                  }
                }
              },
              "required": ["reasoning", "items"]
            }
            """;
    }

    public static class ItemBatchAttributionSchema
    {
        /// <summary>
        /// Injected into the batch prompt via {{response_format}} and shown read-only in the UI.
        /// Keep in sync with JsonSchema below.
        /// </summary>
        public const string JsonExample =
            "[ { \"index\": 0, \"reasoning\": \"brief note on the attribution tag(s) you found, or that there are none\", \"items\": [ " +
            "{ \"index\": 0, \"speaker\": \"Alice\", \"voice_instructions\": \"warm\" }, " +
            "{ \"index\": 2, \"speaker\": \"unknown\", \"voice_instructions\": \"\" } ] } ]";

        /// <summary>
        /// Sent as response_format json_schema so the server constrains generation to this shape.
        /// Wraps the single-response entry in an array with a paragraph index — the outer "index"
        /// names the paragraph, the inner one names an item inside it.
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
                  "items": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "index": { "type": "integer" },
                        "speaker": { "type": "string" },
                        "voice_instructions": { "type": "string" }
                      },
                      "required": ["index", "speaker", "voice_instructions"]
                    }
                  }
                },
                "required": ["index", "reasoning", "items"]
              }
            }
            """;
    }
}
