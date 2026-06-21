using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Normalizes WAV loudness to EBU R128 by shelling out to ffmpeg's <c>loudnorm</c> filter
    /// (single-pass) via CliWrap, round-tripping through GUID-named temp files. Never throws and
    /// never loses audio: any failure returns <see cref="NormalizeStatus.Skipped"/> with the
    /// original rewound audio as a seekable <see cref="MemoryStream"/>.
    /// </summary>
    public class FfmpegAudioNormalizer : IAudioNormalizer
    {
        private const string LoudnormFilter = "loudnorm=I=-16:TP=-1.5:LRA=11";

        private readonly ILogger<FfmpegAudioNormalizer> _logger;

        public FfmpegAudioNormalizer(ILogger<FfmpegAudioNormalizer> logger) => _logger = logger;

        public async Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default)
        {
            var exe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();

            // Buffer the source up front so we can always hand back the original on a skip.
            var original = await ToRewoundMemoryStreamAsync(wav, ct);

            var inputPath = Path.Combine(Path.GetTempPath(), $"r2m-norm-in-{Guid.NewGuid():N}.wav");
            var outputPath = Path.Combine(Path.GetTempPath(), $"r2m-norm-out-{Guid.NewGuid():N}.wav");

            try
            {
                await using (var inputFile = File.Create(inputPath))
                {
                    await original.CopyToAsync(inputFile, ct);
                }
                original.Position = 0;

                var stderr = new StringBuilder();
                try
                {
                    await Cli.Wrap(exe)
                        .WithArguments(new[] { "-y", "-i", inputPath, "-af", LoudnormFilter, outputPath })
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                        .WithValidation(CommandResultValidation.ZeroExitCode)
                        .ExecuteAsync(ct);
                }
                catch (Win32Exception ex)
                {
                    // Executable could not be launched (not found / not executable).
                    _logger.LogWarning(ex, "ffmpeg executable '{Exe}' not found", exe);
                    return new NormalizeResult(NormalizeStatus.Skipped, original,
                        "ffmpeg not found (set path in Audio Processing settings)");
                }
                catch (CommandExecutionException ex)
                {
                    _logger.LogWarning("ffmpeg exited with code {Code}", ex.ExitCode);
                    return new NormalizeResult(NormalizeStatus.Skipped, original,
                        $"ffmpeg failed: {stderr.ToString().Trim()}");
                }

                var normalized = await ReadRewoundAsync(outputPath, ct);
                await original.DisposeAsync();
                return new NormalizeResult(NormalizeStatus.Normalized, normalized, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg normalization failed unexpectedly");
                original.Position = 0;
                return new NormalizeResult(NormalizeStatus.Skipped, original, $"ffmpeg failed: {ex.Message}");
            }
            finally
            {
                TryDelete(inputPath);
                TryDelete(outputPath);
            }
        }

        private static async Task<MemoryStream> ToRewoundMemoryStreamAsync(Stream source, CancellationToken ct)
        {
            var ms = new MemoryStream();
            if (source.CanSeek)
                source.Position = 0;
            await source.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }

        private static async Task<MemoryStream> ReadRewoundAsync(string path, CancellationToken ct)
        {
            var ms = new MemoryStream();
            await using (var file = File.OpenRead(path))
            {
                await file.CopyToAsync(ms, ct);
            }
            ms.Position = 0;
            return ms;
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete temp file '{Path}'", path);
            }
        }
    }
}
