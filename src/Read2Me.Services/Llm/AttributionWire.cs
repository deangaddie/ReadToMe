namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The literal strings of the attribution wire contract: the two item types and the two reserved
    /// speakers. The LLM answers speakers in them, the prompts describe items in them, and context
    /// paragraphs are fed back in them — one definition so the sides cannot drift apart.
    /// <para>
    /// <see cref="Narration"/>/<see cref="Dialog"/> are prompt-side labels only: they describe the
    /// items shown to the model. The answer carries no type, because item boundaries are frozen
    /// (ADR 0005).
    /// </para>
    /// </summary>
    public static class AttributionWire
    {
        public const string Narration = "narration";
        public const string Dialog = "dialog";

        /// <summary>Fixed speaker of every narration item.</summary>
        public const string Narrator = "narrator";

        /// <summary>Sentinel for a dialog item whose speaker is not (yet) known.</summary>
        public const string Unknown = "unknown";

        /// <summary>True when a speaker name is the unknown sentinel rather than a real name.</summary>
        public static bool IsUnknownSpeaker(string speaker) =>
            speaker.Trim().Equals(Unknown, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when a speaker name is the reserved narrator, not a character.</summary>
        public static bool IsNarrator(string speaker) =>
            speaker.Trim().Equals(Narrator, StringComparison.OrdinalIgnoreCase);
    }
}
