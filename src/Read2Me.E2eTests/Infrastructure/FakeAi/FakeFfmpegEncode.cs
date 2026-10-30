using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Infrastructure.FakeAi;

/// <summary>
/// The Setup check's ffmpeg encode, answering at once unless a test calls <see cref="HoldNext"/>:
/// then the encode waits until <see cref="Release"/>, which keeps a Full check in its step while
/// the test joins or cancels it.
/// </summary>
public sealed class FakeFfmpegEncode : IFfmpegEncodeCheck
{
    private TaskCompletionSource? _hold;

    /// <summary>Completes once a held encode has begun.</summary>
    public TaskCompletionSource Entered { get; private set; } = NewSignal();

    public async Task<FfmpegProbeResult> EncodeToneAsync(string? ffmpegPath, CancellationToken ct = default)
    {
        if (_hold is { } hold)
        {
            Entered.TrySetResult();
            await hold.Task.WaitAsync(ct);
        }
        return new FfmpegProbeResult(true, "Encoded a 1 s tone to a 9,000-byte .m4b");
    }

    /// <summary>Fake audio always plays: ffprobe would refuse the fake WAVs the fake AI answers.</summary>
    public Task<FfmpegDurationResult> MeasureAudioAsync(Stream audio, string? ffmpegPath, CancellationToken ct = default) =>
        Task.FromResult(new FfmpegDurationResult(TimeSpan.FromSeconds(1.5)));

    /// <summary>Holds encodes from now on, with a fresh <see cref="Entered"/> to wait on.</summary>
    public void HoldNext()
    {
        Entered = NewSignal();
        _hold = NewSignal();
    }

    /// <summary>Lets a held encode answer, and the next ones answer at once.</summary>
    public void Release()
    {
        _hold?.TrySetResult();
        _hold = null;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
