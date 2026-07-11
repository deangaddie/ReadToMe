namespace Read2Me.AppData.Entities
{
    /// <summary>
    /// Which character-attribution prompt tier an LLM server uses.
    /// </summary>
    public enum AttributionPromptStyle
    {
        /// <summary>Full prompt: inference heuristics (vocatives, alternation, epithets, content clues).</summary>
        Full = 0,

        /// <summary>
        /// Strict prompt for small models: assign a speaker only when the text explicitly names one
        /// in an attribution tag, otherwise answer "unknown" and let the escalation chain take over.
        /// </summary>
        Simple = 1,
    }
}
