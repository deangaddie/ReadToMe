namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Output-token floor for a segment-attribution request. The segment contract makes the answer
    /// grow with the passage: every indexed paragraph is copied back verbatim, split across
    /// segments, each carrying JSON keys, a speaker and voice instructions, plus one reasoning
    /// sentence. A fixed config max_tokens that comfortably fitted the old one-name answer truncates
    /// the segment list of a large batch — a truncated answer is unparseable, so the whole chunk
    /// escalates for no reason (trial finding, ticket 05).
    /// </summary>
    public static class AttributionTokenBudget
    {
        /// <summary>Room for the JSON envelope and a reasoning sentence, per answered paragraph.</summary>
        private const int PerParagraphOverheadTokens = 160;

        /// <summary>Headroom for the request as a whole.</summary>
        private const int BaseTokens = 128;

        /// <summary>
        /// Conservative characters-per-token for copied-back book text: an escaped JSON copy runs
        /// well under 4 chars/token, so 2 leaves slack rather than truncating.
        /// </summary>
        private const int CharsPerToken = 2;

        /// <summary>
        /// The larger of the configured max_tokens and what this passage needs. Never lowers a
        /// generous config, and never caps an unset one (no max_tokens = the server's own limit,
        /// which is what we want).
        /// </summary>
        public static int? ForPassage(int? configured, IEnumerable<string> answeredTexts)
        {
            if (configured is not { } c) return null;

            var needed = BaseTokens;
            foreach (var text in answeredTexts)
                needed += PerParagraphOverheadTokens + text.Length / CharsPerToken;

            return Math.Max(c, needed);
        }
    }
}
