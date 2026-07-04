using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

public sealed class FakeFfmpegProber : IFfmpegProber
{
    public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default)
        => Task.FromResult(new FfmpegProbeResult(true, "fake ffmpeg"));
}
