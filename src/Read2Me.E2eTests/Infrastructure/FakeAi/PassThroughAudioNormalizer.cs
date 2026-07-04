using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>Replaces the ffmpeg-based normalizer so audio flows don't need ffmpeg installed.</summary>
public sealed class PassThroughAudioNormalizer : IAudioNormalizer
{
    public async Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default)
        => new(NormalizeStatus.Normalized, await CopyRewoundAsync(wav, ct), null);

    public Task<Stream> NormalizeToWavAsync(Stream input, string? ffmpegPath, CancellationToken ct = default)
        => CopyRewoundAsync(input, ct);

    private static async Task<Stream> CopyRewoundAsync(Stream input, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }
}
