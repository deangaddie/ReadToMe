namespace Read2Me.Services.Characters
{
    /// <summary>One character proposed by character discovery: a primary name and any aliases.</summary>
    public sealed record DiscoveredCharacter(string Name, IReadOnlyList<string> Aliases);

    public static class CharacterDiscoverySchema
    {
        /// <summary>Injected into the discovery prompt via {{response_format}} and shown read-only in the UI.</summary>
        public const string JsonExample =
            "{ \"reasoning\": \"brief note on how you identified the cast\", " +
            "\"characters\": [ { \"name\": \"Bilbo Baggins\", \"aliases\": [\"Bilbo\", \"Mr. Baggins\"] } ] }";

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
                "characters": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "name": { "type": "string" },
                      "aliases": { "type": "array", "items": { "type": "string" } }
                    },
                    "required": ["name", "aliases"]
                  }
                }
              },
              "required": ["reasoning", "characters"]
            }
            """;
    }
}
