namespace Read2Me.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's context budget. The prompt is the whole chapter so far, so a
    /// long chapter outgrows the 16k <c>gemma-12b</c> context. When it would, the oldest half of the
    /// lines before the current paragraph is dropped in one step; the trim start then stays put
    /// until the next overflow, so llama's prompt cache reuses the prefix again after one re-prefill.
    /// </summary>
    internal static class ChapterPassBudget
    {
        /// <summary>
        /// The whole prompt's ceiling in characters: ≈ 10–12.5k tokens at 3.2–4 chars a token, which
        /// leaves room for the answer within the 16 384-token <c>gemma-12b</c> preset. A constant sized
        /// for that preset; conservative on a 32k model.
        /// </summary>
        public const int MaxPromptChars = 40_000;

        /// <summary>Whether a prompt of these parts is over <see cref="MaxPromptChars"/>.</summary>
        public static bool NeedsTrim(int systemChars, int passageChars, int questionChars) =>
            systemChars + passageChars + questionChars > MaxPromptChars;

        /// <summary>
        /// The next trim start: the midpoint of [<paramref name="trimStart"/>, <paramref name="currentK"/>),
        /// rounded up so a trim with any line to drop drops at least one. Never past the current paragraph.
        /// </summary>
        public static int NextTrimStart(int trimStart, int currentK) =>
            currentK <= trimStart ? trimStart : trimStart + (currentK - trimStart + 1) / 2;
    }
}
