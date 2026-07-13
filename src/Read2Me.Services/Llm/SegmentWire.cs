namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The literal strings of the segment-attribution wire contract: the two segment types and the
    /// two reserved speakers. The LLM answers in them, the prompts document them, and context
    /// paragraphs are fed back in them — one definition so the sides cannot drift apart.
    /// </summary>
    public static class SegmentWire
    {
        public const string Narration = "narration";
        public const string Dialog = "dialog";

        /// <summary>Fixed speaker of every narration segment.</summary>
        public const string Narrator = "narrator";

        /// <summary>Sentinel for a dialog segment whose speaker is not (yet) known.</summary>
        public const string Unknown = "unknown";
    }
}
