using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Probes ffmpeg and ffprobe by spawning <c>-version</c>. A null/blank path resolves the tool via PATH.
    /// A tool that can't be started is reported by where it was looked for, not by the OS error.
    /// </summary>
    public class FfmpegProber : IFfmpegProber
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly ILogger<FfmpegProber> _logger;

        public FfmpegProber(ILogger<FfmpegProber> logger) => _logger = logger;

        public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
            VersionAsync("ffmpeg", string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim(), ct);

        public Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default) =>
            VersionAsync("ffprobe", ResolveFfprobe(ffmpegPath), ct);

        /// <summary>
        /// Resolves ffprobe path. When ffmpegPath is set, replaces the filename with "ffprobe"
        /// (same directory). Falls back to "ffprobe" on PATH otherwise.
        /// </summary>
        public static string ResolveFfprobe(string? ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                return "ffprobe";

            var trimmed = ffmpegPath.Trim();
            var dir = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(dir))
                return "ffprobe";

            var ext = Path.GetExtension(trimmed);
            return Path.Combine(dir, $"ffprobe{ext}");
        }

        private async Task<FfmpegProbeResult> VersionAsync(string tool, string exe, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(ex, "{Tool} probe at '{Exe}' failed to start", tool, exe);
                return new FfmpegProbeResult(false, Path.IsPathRooted(exe) ? $"not found at {exe}" : "not on PATH");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode == 0)
                {
                    var banner = FirstLine(stdout);
                    return new FfmpegProbeResult(true, string.IsNullOrWhiteSpace(banner) ? $"{tool} OK" : banner);
                }

                _logger.LogWarning("{Tool} probe at '{Exe}' exited with code {Code}", tool, exe, process.ExitCode);
                var message = !string.IsNullOrWhiteSpace(stderr) ? FirstLine(stderr) : $"{tool} exited with code {process.ExitCode}";
                return new FfmpegProbeResult(false, message);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return new FfmpegProbeResult(false, $"{tool} didn't answer within {Timeout.TotalSeconds:0} s");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Tool} probe at '{Exe}' failed", tool, exe);
                return new FfmpegProbeResult(false, ex.Message);
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort: it may have exited between the check and the kill.
            }
        }

        private static string FirstLine(string text)
        {
            var nl = text.IndexOfAny(new[] { '\r', '\n' });
            return (nl < 0 ? text : text.Substring(0, nl)).Trim();
        }
    }
}
