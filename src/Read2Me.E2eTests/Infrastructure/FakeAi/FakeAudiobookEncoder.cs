using Read2Me.Services.Audio.Assembly;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// Stands in for ffmpeg so an assembly run reaches every phase without it: durations are a fixed
/// second, silence is an empty temp file, and the "encode" reports progress in quarters and writes
/// a few bytes where the m4b goes. <see cref="HoldAtHalf"/> parks the encode at 50% so a test can
/// watch it mid-flight or cancel it — deterministically, not inside a timing window.
/// </summary>
public sealed class FakeAudiobookEncoder : IAudiobookEncoder
{
    /// <summary>
    /// When set, the encode reports 50% and then waits for this to complete (or for the run to be
    /// cancelled) before it carries on. The test that sets it releases and clears it.
    /// </summary>
    public TaskCompletionSource? HoldAtHalf { get; set; }

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
            ct.ThrowIfCancellationRequested();
            progress?.Report(quarter / 4.0);
            if (quarter == 2 && HoldAtHalf is { } hold)
                await hold.Task.WaitAsync(ct);
        }
        await File.WriteAllBytesAsync(outputPath, "fake m4b"u8.ToArray(), ct);
    }
}
