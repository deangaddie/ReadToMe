using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Probes ffmpeg by spawning <c>ffmpeg -version</c>. A null/blank path resolves "ffmpeg" via PATH.
    /// </summary>
    public class FfmpegProber : IFfmpegProber
    {
        private readonly ILogger<FfmpegProber> _logger;

        public FfmpegProber(ILogger<FfmpegProber> logger) => _logger = logger;

        public async Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default)
        {
            var exe = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode == 0)
                {
                    var banner = FirstLine(stdout);
                    return new FfmpegProbeResult(true, string.IsNullOrWhiteSpace(banner) ? "ffmpeg OK" : banner);
                }

                _logger.LogWarning("ffmpeg probe at '{Exe}' exited with code {Code}", exe, process.ExitCode);
                var message = !string.IsNullOrWhiteSpace(stderr) ? FirstLine(stderr) : $"ffmpeg exited with code {process.ExitCode}";
                return new FfmpegProbeResult(false, message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg probe at '{Exe}' failed", exe);
                return new FfmpegProbeResult(false, ex.Message);
            }
        }

        private static string FirstLine(string text)
        {
            var nl = text.IndexOfAny(new[] { '\r', '\n' });
            return (nl < 0 ? text : text.Substring(0, nl)).Trim();
        }
    }
}
