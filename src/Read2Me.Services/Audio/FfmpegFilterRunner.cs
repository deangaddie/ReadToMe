using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Runs a single ffmpeg filtergraph over a WAV buffer on behalf of an
    /// <see cref="IAudioPostProcessStep"/>, writing <see cref="CanonicalWav"/>. Shared by every
    /// step, so they all honour the same never-throw / never-lose-audio contract: a missing exe,
    /// an unsupported filter, a non-zero exit, or a timeout comes back as the input audio
    /// unchanged with <see cref="PostProcessResult.Applied"/> false and a reason. Only caller
    /// cancellation propagates.
    /// </summary>
    public static class FfmpegFilterRunner
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        public static async Task<PostProcessResult> RunAsync(
            string stepId, byte[] wav, string? ffmpegPath, string filter, ILogger logger, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var exe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
            var inputPath = TempPath(stepId, "in");
            var outputPath = TempPath(stepId, "out");

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
                            .Concat(CanonicalWav.FormatArgs)
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
                    logger.LogWarning("{StepId} ffmpeg timed out after {Seconds}s", stepId, Timeout.TotalSeconds);
                    return Skip(wav, "ffmpeg timed out");
                }
                catch (Win32Exception ex)
                {
                    logger.LogWarning(ex, "{StepId} ffmpeg executable '{Exe}' not found", stepId, exe);
                    return Skip(wav, "ffmpeg not found (set path in Audio Processing settings)");
                }
                catch (CommandExecutionException ex)
                {
                    var trimmed = stderr.ToString().Trim();
                    var msg = trimmed.Length > 500 ? trimmed[..500] : trimmed;
                    logger.LogWarning("{StepId} ffmpeg exited with code {Code}: {Stderr}", stepId, ex.ExitCode, msg);
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
                logger.LogWarning(ex, "{StepId} failed unexpectedly", stepId);
                return Skip(wav, $"ffmpeg failed: {ex.Message}");
            }
            finally
            {
                TryDelete(inputPath, logger);
                TryDelete(outputPath, logger);
            }
        }

        private static PostProcessResult Skip(byte[] input, string reason) =>
            new(input, Applied: false, Reason: reason);

        private static string TempPath(string stepId, string suffix) =>
            Path.Combine(Path.GetTempPath(), $"r2m-{stepId}-{suffix}-{Guid.NewGuid():N}.wav");

        private static void TryDelete(string path, ILogger logger)
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
