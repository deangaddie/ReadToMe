using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The one shipped post-process step: tames harsh sibilants/plosives in TTS output with an
    /// ffmpeg consonant-soften filter chain (see <see cref="ConsonantSoftenChainBuilder"/>).
    /// Honours the never-throw / never-lose-audio contract: any ffmpeg failure (missing exe,
    /// unsupported filter, non-zero exit, timeout) returns the input unchanged with
    /// <see cref="PostProcessResult.Applied"/> false and a reason. Output is written in the
    /// canonical WAV format (24 kHz mono 16-bit PCM), same as the normalizer.
    /// </summary>
    public class ConsonantSoftenStep(ILogger<ConsonantSoftenStep> logger) : IAudioPostProcessStep
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        private static readonly string[] CanonicalFormatArgs = { "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le" };

        public string StepId => AudioPostProcessStepIds.ConsonantSoften;

        public async Task<PostProcessResult> ProcessAsync(
            byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var settings = ParseSettings(settingsJson);
            var filter = ConsonantSoftenChainBuilder.Build(settings);
            var exe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();

            var inputPath = TempPath("r2m-cs-in");
            var outputPath = TempPath("r2m-cs-out");

            try
            {
                await File.WriteAllBytesAsync(inputPath, wav, ct);

                var stderr = new StringBuilder();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(Timeout);

                try
                {
                    await Cli.Wrap(exe)
                        .WithArguments(new[] { "-y", "-i", inputPath, "-af", filter }
                            .Concat(CanonicalFormatArgs)
                            .Append(outputPath))
                        .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                        .WithValidation(CommandResultValidation.ZeroExitCode)
                        .ExecuteAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("consonant-soften ffmpeg timed out after {Seconds}s", Timeout.TotalSeconds);
                    return Skip(wav, "ffmpeg timed out");
                }
                catch (Win32Exception ex)
                {
                    logger.LogWarning(ex, "consonant-soften ffmpeg executable '{Exe}' not found", exe);
                    return Skip(wav, "ffmpeg not found (set path in Audio Processing settings)");
                }
                catch (CommandExecutionException ex)
                {
                    var trimmed = stderr.ToString().Trim();
                    var msg = trimmed.Length > 500 ? trimmed[..500] : trimmed;
                    logger.LogWarning("consonant-soften ffmpeg exited with code {Code}: {Stderr}", ex.ExitCode, msg);
                    return Skip(wav, $"ffmpeg failed: {msg}");
                }

                var filtered = await File.ReadAllBytesAsync(outputPath, ct);
                return new PostProcessResult(filtered, Applied: true, Reason: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "consonant-soften failed unexpectedly");
                return Skip(wav, $"ffmpeg failed: {ex.Message}");
            }
            finally
            {
                TryDelete(inputPath);
                TryDelete(outputPath);
            }
        }

        private static PostProcessResult Skip(byte[] input, string reason) =>
            new(input, Applied: false, Reason: reason);

        private ConsonantSoftenSettings? ParseSettings(string? settingsJson)
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<ConsonantSoftenSettings>(settingsJson, AudioPostProcessJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "consonant-soften settings JSON malformed; using defaults");
                return null;
            }
        }

        private static string TempPath(string prefix) =>
            Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.wav");

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to delete temp file '{Path}'", path);
            }
        }
    }
}
