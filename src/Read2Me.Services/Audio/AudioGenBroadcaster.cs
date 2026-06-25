namespace Read2Me.Services.Audio
{
    public abstract record AudioGenEvent;

    /// First event for every processed item, published before any work. Character/text
    /// may be null when not yet resolved (e.g. row not found).
    public sealed record ItemStarted(Guid Id, int Attempt, string? Character, string? Text) : AudioGenEvent;
    public sealed record AudioGenerated(Guid Id, int Attempt) : AudioGenEvent;
    public sealed record Normalized(Guid Id, int Attempt, bool Ok, string? Reason) : AudioGenEvent;
    public sealed record Transcribed(Guid Id, int Attempt, string Transcript) : AudioGenEvent;
    public sealed record Verified(Guid Id, int Attempt, bool Ok, double? Wer, string? Reason, bool Rescued = false) : AudioGenEvent;
    public sealed record Failed(Guid Id, int Attempt, string Reason) : AudioGenEvent;

    /// Singleton bridge: scoped audio queue processor publishes; stream view subscribes.
    public sealed class AudioGenBroadcaster
    {
        public event Action<AudioGenEvent>? Event;
        public void Publish(AudioGenEvent e) => Event?.Invoke(e);
    }
}
