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

        /// <summary>
        /// Chapter pass: one dialog item at a time, sequentially through the chapter, with the whole
        /// chapter so far (plus a short look-ahead) as context and earlier answers shown inline. The
        /// answer is grammar-restricted to a roster name or "Unknown" plus a delivery cue; thinking is
        /// off and sampling greedy whatever the config says. Routed by <c>StyleRoutedChainStep</c>.
        /// </summary>
        Chapter = 2,
    }
}
