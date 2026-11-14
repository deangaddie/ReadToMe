using Read2Me.Services.Audio;

namespace Read2Me.Tests.Fakes
{
    public sealed class FakeAudioItemPipeline : IAudioItemPipeline
    {
        public PipelineResult? Result { get; set; }
        public Exception? Throws { get; set; }
        public PipelineRequest? LastRequest { get; private set; }

        /// <summary>Runs as the take generates — before the fake answers, and before it honours <c>ct</c> as the real pipeline does.</summary>
        public Action? WhileRunning { get; set; }

        public Task<PipelineResult> RunAsync(PipelineRequest request, CancellationToken ct)
        {
            LastRequest = request;
            WhileRunning?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result!);
        }
    }
}
