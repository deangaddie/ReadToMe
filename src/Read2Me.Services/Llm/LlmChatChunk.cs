namespace Read2Me.Services.Llm
{
    /// <summary>
    /// A single streamed chunk from the LLM. It may carry text (<see cref="Thinking"/> or
    /// <see cref="Content"/>), server metrics (<see cref="Timings"/> or <see cref="Usage"/>),
    /// both, or neither on the terminating chunk.
    /// </summary>
    /// <remarks>
    /// llama.cpp's metrics-bearing chunk is <b>delta-less</b>: the chunk carrying
    /// <see cref="Timings"/>/<see cref="Usage"/> has an empty delta or no choices at all, so a
    /// metrics chunk with all-null text is normal and must not be treated as empty.
    /// </remarks>
    public sealed record LlmChatChunk(
        string? Thinking,
        string? Content,
        bool Done,
        LlmTimings? Timings = null,
        LlmUsage? Usage = null);
}
