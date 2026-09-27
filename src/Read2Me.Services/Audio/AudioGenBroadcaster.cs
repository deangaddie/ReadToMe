namespace Read2Me.Services.Audio
{
    public abstract record AudioGenEvent;

    /// First event for every processed item, published before any work. Character/text
    /// may be null when not yet resolved (e.g. row not found).
    public sealed record ItemStarted(Guid Id, int Attempt, string? Character, string? Text) : AudioGenEvent;
    public sealed record AudioGenerated(Guid Id, int Attempt) : AudioGenEvent;
    public sealed record Normalized(Guid Id, int Attempt, bool Ok, string? Reason) : AudioGenEvent;
    /// <summary>
    /// One post-process step run. <c>Applied</c> false means the step fell back to its input
    /// audio; <c>Reason</c> is set only then.
    /// </summary>
    public sealed record PostProcessed(Guid Id, int Attempt, string StepId, bool Applied, string? Reason) : AudioGenEvent;
    public sealed record Transcribed(Guid Id, int Attempt, string Transcript) : AudioGenEvent;
    public sealed record Verified(Guid Id, int Attempt, bool Ok, double? Wer, string? Reason, bool Rescued = false) : AudioGenEvent;
    public sealed record Failed(Guid Id, int Attempt, string Reason) : AudioGenEvent;

    /// <summary>
    /// The TTS runtime is about to load <c>ModelId</c> to serve the next request (a cold first
    /// request takes 9–31 s). Not tied to an item: the gate that sees it sits below the item
    /// pipeline, so a consumer attaches it to whichever item is generating.
    /// </summary>
    public sealed record TtsModelLoading(string ModelId) : AudioGenEvent;


}
