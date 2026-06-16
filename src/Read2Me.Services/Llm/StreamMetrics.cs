namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Rough token accounting for a single streamed completion. Estimates ~4 chars/token.
    /// Pure: feed it text + elapsed seconds, read back rates. No timers, no UI.
    /// </summary>
    public sealed class StreamMetrics
    {
        public int TokensIn { get; }
        public int TokensOut { get; private set; }

        public StreamMetrics(string prompt) => TokensIn = EstimateTokens(prompt);

        public void AddOutput(string text) => TokensOut += EstimateTokens(text);

        public double TokensPerSecond(double elapsedSeconds) =>
            elapsedSeconds > 0 ? TokensOut / elapsedSeconds : 0;

        public static int EstimateTokens(string text) =>
            string.IsNullOrEmpty(text) ? 0 : (int)System.Math.Ceiling(text.Length / 4.0);
    }
}
