using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    public record ResolutionResult(
        string? Speaker,
        string? SourceText,
        PipelineRequest? Request,
        string? FailureReason)
    {
        public bool Succeeded => Request is not null;
    }

    public interface IAudioItemResolver
    {
        Task<ResolutionResult> ResolveAsync(QueuedAudioItem item, CancellationToken ct);
    }
}
