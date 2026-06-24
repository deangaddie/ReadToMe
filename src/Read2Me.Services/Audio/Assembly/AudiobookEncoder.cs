using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio.Assembly
{
    /// <summary>
    /// CliWrap-based implementation of <see cref="IAudiobookEncoder"/>.
    ///
    /// Duration probing uses ffprobe (resolved as a sibling of the ffmpeg exe, or "ffprobe" on
    /// PATH when ffmpegPath is null/blank). This avoids overloading the existing
    /// <c>FfmpegProber</c> / <c>IFfmpegProber</c> which only runs a health-check
    /// (<c>ffmpeg -version</c>) and returns no durations.
    ///
    /// Silence files are cached per distinct ms in a <see cref="ConcurrentDictionary"/> for the
    /// lifetime of one encoder instance (≈ one assembly run).
    /// </summary>
    public sealed class AudiobookEncoder : IAudiobookEncoder
    {
        private readonly ILogger<AudiobookEncoder> _logger;

        // Cache: ms → absolute temp path of the generated silence WAV.
        private readonly ConcurrentDictionary<int, string> _silenceCache = new();

        public AudiobookEncoder(ILogger<AudiobookEncoder> logger) => _logger = logger;

        // ── 1. Duration probe ────────────────────────────────────────────────

        public async Task<TimeSpan> GetDurationAsync(string wavPath, string? ffmpegPath, CancellationToken ct = default)
        {
            var ffprobe = ResolveFfprobe(ffmpegPath);
            var stderr = new StringBuilder();
            var stdout = new StringBuilder();

            try
            {
                await Cli.Wrap(ffprobe)
                    .WithArguments(new[]
                    {
                        "-v", "error",
                        "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1",
                        wavPath
                    })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync(ct);
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException(
                    "ffmpeg is required to process voice audio (set the path in Audio Processing settings).");
            }
            catch (CommandExecutionException)
            {
                var tail = TailStderr(stderr.ToString());
                throw new InvalidOperationException($"ffprobe failed: {tail}");
            }

            var raw = stdout.ToString().Trim();
            if (double.TryParse(raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var secs))
                return TimeSpan.FromSeconds(secs);

            throw new InvalidOperationException(
                $"ffprobe returned unexpected duration output: '{raw}'");
        }

        // ── 2. Silence generation (cached) ───────────────────────────────────

        public async Task<string> GetSilenceAsync(int ms, string? ffmpegPath, CancellationToken ct = default)
        {
            // Fast path: already generated this run.
            if (_silenceCache.TryGetValue(ms, out var cached) && File.Exists(cached))
                return cached;

            var exe = ResolveExe(ffmpegPath);
            var outPath = TempPath("r2m-silence", "wav");
            var seconds = ms / 1000.0;
            var stderr = new StringBuilder();

            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(new[]
                    {
                        "-y",
                        "-f", "lavfi",
                        "-i", $"anullsrc=r=24000:cl=mono",
                        "-t", seconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                        "-c:a", "pcm_s16le",
                        outPath
                    })
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync(ct);
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException(
                    "ffmpeg is required to process voice audio (set the path in Audio Processing settings).");
            }
            catch (CommandExecutionException)
            {
                TryDelete(outPath);
                var tail = TailStderr(stderr.ToString());
                throw new InvalidOperationException($"ffmpeg silence generation failed: {tail}");
            }

            _silenceCache[ms] = outPath;
            return outPath;
        }

        // ── 3. Concat-encode to m4b ──────────────────────────────────────────

        public async Task EncodeAsync(
            string concatListPath,
            string ffmetadataPath,
            string? coverImagePath,
            string outputPath,
            TimeSpan totalDuration,
            IProgress<double>? progress,
            string? ffmpegPath,
            CancellationToken ct = default)
        {
            var exe = ResolveExe(ffmpegPath);
            var args = BuildEncodeArgs(concatListPath, ffmetadataPath, coverImagePath, outputPath);

            var stderr = new StringBuilder();

            var stderrPipe = progress is not null
                ? PipeTarget.Merge(
                    PipeTarget.ToStringBuilder(stderr),
                    PipeTarget.ToDelegate(line =>
                    {
                        var fraction = FfmpegProgressParser.ParseProgress(line, totalDuration);
                        if (fraction.HasValue)
                            progress.Report(fraction.Value);
                    }))
                : PipeTarget.ToStringBuilder(stderr);

            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(args)
                    .WithStandardErrorPipe(stderrPipe)
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync(ct);

                progress?.Report(1.0);
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException(
                    "ffmpeg is required to process voice audio (set the path in Audio Processing settings).");
            }
            catch (CommandExecutionException)
            {
                var tail = TailStderr(stderr.ToString());
                throw new InvalidOperationException($"ffmpeg encode failed: {tail}");
            }
        }

        // ── Arg builder (testability + clarity) ──────────────────────────────

        internal static string[] BuildEncodeArgs(
            string concatListPath,
            string ffmetadataPath,
            string? coverImagePath,
            string outputPath)
        {
            var args = new List<string>
            {
                "-y",
                // Concat demuxer as primary audio input (index 0)
                "-f", "concat", "-safe", "0", "-i", concatListPath,
                // ffmetadata as second input (index 1)
                "-i", ffmetadataPath,
            };

            bool hasCover = !string.IsNullOrEmpty(coverImagePath);
            if (hasCover)
            {
                // Cover image as third input (index 2)
                args.AddRange(new[] { "-i", coverImagePath! });
            }

            // Map audio from concat input
            args.AddRange(new[] { "-map", "0:a" });

            if (hasCover)
            {
                // coverIndex = 2 (0=concat, 1=ffmetadata, 2=cover)
                args.AddRange(new[]
                {
                    "-map", "2:v",
                    "-c:v", "mjpeg",
                    "-disposition:v", "attached_pic",
                });
            }

            args.AddRange(new[]
            {
                // Apply global metadata from the ffmetadata input
                "-map_metadata", "1",
                // Audio encode; -f ipod = m4b/m4a container (needed when output has .tmp extension)
                "-c:a", "aac", "-b:a", "64k", "-ac", "1", "-ar", "24000",
                "-f", "ipod",
                outputPath
            });

            return args.ToArray();
        }

        // ── Path resolution ──────────────────────────────────────────────────

        private static string ResolveExe(string? ffmpegPath) =>
            string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();

        /// <summary>
        /// Resolves ffprobe path. When ffmpegPath is set, replaces the filename with "ffprobe"
        /// (same directory). Falls back to "ffprobe" on PATH otherwise.
        /// </summary>
        private static string ResolveFfprobe(string? ffmpegPath)
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

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string TempPath(string prefix, string ext) =>
            Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.{ext}");

        private static string TailStderr(string stderr)
        {
            var trimmed = stderr.Trim();
            return trimmed.Length > 500 ? trimmed[^500..] : trimmed;
        }

        private void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete temp file '{Path}'", path); }
        }
    }
}
