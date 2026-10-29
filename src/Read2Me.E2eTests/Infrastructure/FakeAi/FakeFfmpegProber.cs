using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>An ffmpeg that always answers, unless a test sets <see cref="Missing"/> to make both tools fail.</summary>
public sealed class FakeFfmpegProber : IFfmpegProber
{
    public bool Missing { get; set; }

    public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default)
        => Answer("fake ffmpeg");

    public Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default)
        => Answer("fake ffprobe");

    private Task<FfmpegProbeResult> Answer(string banner) => Task.FromResult(Missing
        ? new FfmpegProbeResult(false, "The system cannot find the file specified.")
        : new FfmpegProbeResult(true, banner));
}
