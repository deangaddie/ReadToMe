namespace Read2Me.Services.Llm
{
    /// <summary>
    /// A single streamed delta from the LLM. Either <see cref="Thinking"/> or
    /// <see cref="Content"/> (or neither, on the terminating chunk) carries text.
    /// </summary>
    public sealed record LlmChatChunk(string? Thinking, string? Content, bool Done);
}
