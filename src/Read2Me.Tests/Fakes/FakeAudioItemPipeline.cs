using Read2Me.Services.Audio;

namespace Read2Me.Tests.Fakes
{
    public sealed class FakeAudioItemPipeline : IAudioItemPipeline
    {
        public PipelineResult? Result { get; set; }
        public Exception? Throws { get; set; }
        public PipelineRequest? LastRequest { get; private set; }

        public Task<PipelineResult> RunAsync(PipelineRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result!);
        }
    }
}
