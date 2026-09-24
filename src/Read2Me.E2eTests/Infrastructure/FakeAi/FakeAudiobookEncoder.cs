using Read2Me.Services.Audio.Assembly;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// Stands in for ffmpeg so an assembly run reaches every phase without it: durations are a fixed
/// second, silence is an empty temp file, and the "encode" reports progress in quarters and writes
/// a few bytes where the m4b goes. <see cref="EncodeDelay"/> stretches the encode (per quarter) so
/// a test can watch progress or cancel mid-encode.
/// </summary>
public sealed class FakeAudiobookEncoder : IAudiobookEncoder
{
    public TimeSpan EncodeDelay { get; set; } = TimeSpan.Zero;

    public Task<TimeSpan> GetDurationAsync(string wavPath, string? ffmpegPath, CancellationToken ct = default) =>
        Task.FromResult(TimeSpan.FromSeconds(1));

    public Task<string> GetSilenceAsync(int ms, string? ffmpegPath, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"r2m-e2e-silence-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, []);
        return Task.FromResult(path);
    }

    public async Task EncodeAsync(
        string concatListPath, string ffmetadataPath, string? coverImagePath, string outputPath,
        TimeSpan totalDuration, IProgress<double>? progress, string? ffmpegPath, CancellationToken ct = default)
    {
        for (var quarter = 1; quarter <= 4; quarter++)
        {
            await Task.Delay(EncodeDelay, ct);
            progress?.Report(quarter / 4.0);
        }
        await File.WriteAllBytesAsync(outputPath, "fake m4b"u8.ToArray(), ct);
    }
}
