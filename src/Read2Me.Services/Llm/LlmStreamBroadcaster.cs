namespace Read2Me.Services.Llm
{
    public abstract record LlmStreamEvent;
    public sealed record RequestStarted(string ParagraphPreview, string Prompt) : LlmStreamEvent;
    public sealed record ThinkingDelta(string Text) : LlmStreamEvent;
    public sealed record ContentDelta(string Text) : LlmStreamEvent;
    public sealed record StreamCompleted(int TokensIn, int TokensOut,
        double ElapsedSeconds, double TokensPerSecond) : LlmStreamEvent;
    public sealed record StreamFailed(string Reason) : LlmStreamEvent;

    /// Singleton bridge: scoped attribution service publishes; dialog subscribes.
    public sealed class LlmStreamBroadcaster
    {
        public event Action<LlmStreamEvent>? Event;
        public void Publish(LlmStreamEvent e) => Event?.Invoke(e);
    }
}
