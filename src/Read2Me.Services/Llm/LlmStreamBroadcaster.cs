namespace Read2Me.Services.Llm
{
    public abstract record LlmStreamEvent;
    public sealed record RequestStarted(string ParagraphPreview, string Prompt) : LlmStreamEvent;
    public sealed record ThinkingDelta(string Text) : LlmStreamEvent;
    public sealed record ContentDelta(string Text) : LlmStreamEvent;
    public sealed record StreamCompleted(int TokensIn, int TokensOut,
        double ElapsedSeconds, double TokensPerSecond) : LlmStreamEvent;
    public sealed record StreamFailed(string Reason) : LlmStreamEvent;

    /// <summary>
    /// Announces that the attribution chain is about to run a step ≥ 1 (an escalation).
    /// Published before each escalation step with the 1-based step index, the config being
    /// tried, and the count of suspect items entering that step. Step 0 (the primary) is not
    /// announced. The stream-panel subscriber renders or ignores it gracefully.
    /// </summary>
    public sealed record EscalationStarted(int Step, string ConfigName, int ItemCount) : LlmStreamEvent;
}
