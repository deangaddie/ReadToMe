using Read2Me.Core.Models;
using Read2Me.Services.Audio;

namespace Read2Me.Tests.Fakes
{
    public sealed class FakeAudioItemResolver : IAudioItemResolver
    {
        public ResolutionResult? Result { get; set; }
        public Exception? Throws { get; set; }
        public QueuedAudioItem? LastItem { get; private set; }

        public Task<ResolutionResult> ResolveAsync(QueuedAudioItem item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastItem = item;
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result!);
        }
    }
}
