using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Proves an ffmpeg build can do what audio generation and assembly ask of it: a 1 s generated
    /// tone through the post-process filters (adynamicequalizer and the alimiter tail) encoded to
    /// AAC in an .m4b. A build missing a filter or the encoder fails here instead of mid-book.
    /// </summary>
    public interface IFfmpegEncodeCheck
    {
        /// <summary>A null/blank path runs ffmpeg from PATH. Never throws but for caller cancellation.</summary>
        Task<FfmpegProbeResult> EncodeToneAsync(string? ffmpegPath, CancellationToken ct = default);

        /// <summary>
        /// How long <paramref name="audio"/> plays, as the ffprobe beside the given ffmpeg reads it (a
        /// null/blank path: ffprobe from PATH). Never throws but for caller cancellation.
        /// </summary>
        Task<FfmpegDurationResult> MeasureAudioAsync(Stream audio, string? ffmpegPath, CancellationToken ct = default);
    }

    /// <summary>What ffprobe read: the duration, or null with why in <see cref="Error"/>.</summary>
    public sealed record FfmpegDurationResult(TimeSpan? Duration, string? Error = null);

    public sealed class FfmpegEncodeCheck(ILogger<FfmpegEncodeCheck> logger) : IFfmpegEncodeCheck
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>The consonant-soften chain at its defaults: adynamicequalizer, then the mandatory alimiter.</summary>
        private static readonly string Filters = ConsonantSoftenChainBuilder.Build(null);

        public async Task<FfmpegProbeResult> EncodeToneAsync(string? ffmpegPath, CancellationToken ct = default)
        {
            var exe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
            var output = Path.Combine(Path.GetTempPath(), $"r2m-setup-check-{Guid.NewGuid():N}.m4b");
            var stderr = new StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await Cli.Wrap(exe)
                    .WithArguments([
                        "-hide_banner", "-y",
                        "-f", "lavfi", "-i", "sine=frequency=440:duration=1:sample_rate=24000",
                        "-af", Filters,
                        "-c:a", "aac", "-b:a", "64k", "-f", "ipod", output,
                    ])
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync(timeout.Token);

                var bytes = new FileInfo(output).Length;
                return bytes > 0
                    ? new FfmpegProbeResult(true, $"Encoded a 1 s tone to a {bytes:N0}-byte .m4b")
                    : new FfmpegProbeResult(false, "ffmpeg wrote an empty .m4b");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new FfmpegProbeResult(false, $"ffmpeg didn't finish within {Timeout.TotalSeconds:0} s");
            }
            catch (Win32Exception)
            {
                return new FfmpegProbeResult(false, Path.IsPathRooted(exe) ? $"not found at {exe}" : "not on PATH");
            }
            catch (CommandExecutionException ex)
            {
                // ffmpeg's last stderr line names the missing filter or encoder.
                var last = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault();
                logger.LogWarning("Setup check encode exited with code {Code}: {Stderr}", ex.ExitCode, last);
                return new FfmpegProbeResult(false, last ?? $"ffmpeg exited with code {ex.ExitCode}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Setup check encode failed");
                return new FfmpegProbeResult(false, ex.Message);
            }
            finally
            {
                DeleteQuietly(output);
            }
        }

        public async Task<FfmpegDurationResult> MeasureAudioAsync(Stream audio, string? ffmpegPath, CancellationToken ct = default)
        {
            var file = Path.Combine(Path.GetTempPath(), $"r2m-setup-check-{Guid.NewGuid():N}.wav");
            try
            {
                await using (var written = File.Create(file))
                    await audio.CopyToAsync(written, ct);
                return new FfmpegDurationResult(await Assembly.AudiobookEncoder.ReadDurationAsync(file, ffmpegPath, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Setup check couldn't measure the audio");
                return new FfmpegDurationResult(null, ex.Message);
            }
            finally
            {
                DeleteQuietly(file);
            }
        }

        private static void DeleteQuietly(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best effort: a temp file left behind is harmless.
            }
        }
    }
}
