using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Five values captured from ffmpeg's loudnorm pass-1 JSON output.
    /// </summary>
    internal record LoudnormMeasure(
        string InputI,
        string InputTp,
        string InputLra,
        string InputThresh,
        string TargetOffset);

    /// <summary>
    /// Normalizes WAV loudness to EBU R128 (<c>I=-16:TP=-1.5:LRA=11</c>) by shelling out to
    /// ffmpeg's <c>loudnorm</c> filter via CliWrap, using a two-pass approach:
    /// <list type="number">
    ///   <item>Measure pass — captures loudness statistics from ffmpeg stderr JSON.</item>
    ///   <item>Apply pass — re-runs loudnorm with the measured values and <c>linear=true</c>.</item>
    /// </list>
    /// If the pass-1 JSON cannot be parsed, falls back to a single-pass <c>loudnorm</c> apply
    /// (less accurate but still levelled). Only a hard ffmpeg/exe failure returns
    /// <see cref="NormalizeStatus.Skipped"/> with the original rewound audio intact. Never throws
    /// (except <see cref="OperationCanceledException"/>) and never loses audio.
    /// <para>
    /// Both paths write the canonical WAV format (24 kHz mono 16-bit PCM). loudnorm runs at an
    /// internal 192 kHz, so omitting the format args would store 192 kHz audio.
    /// </para>
    /// </summary>
    public class FfmpegAudioNormalizer : IAudioNormalizer
    {
        private const string LoudnormBase = "loudnorm=I=-16:TP=-1.5:LRA=11";
        private static string[] CanonicalFormatArgs => CanonicalWav.FormatArgs;

        private readonly ILogger<FfmpegAudioNormalizer> _logger;

        public FfmpegAudioNormalizer(ILogger<FfmpegAudioNormalizer> logger) => _logger = logger;

        public async Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default)
        {
            var exe = ResolveExe(ffmpegPath);
            var original = await ToRewoundMemoryStreamAsync(wav, ct);

            var inputPath = TempPath("r2m-norm-in", "wav");
            var outputPath = TempPath("r2m-norm-out", "wav");

            try
            {
                await WriteToFileAsync(original, inputPath, ct);
                original.Position = 0;

                var (measureResult, measure) = await RunMeasurePassAsync(exe, inputPath, ct);

                if (measureResult == MeasureOutcome.ExeNotFound)
                    return new NormalizeResult(NormalizeStatus.Skipped, original,
                        "ffmpeg not found (set path in Audio Processing settings)");

                if (measureResult == MeasureOutcome.FfmpegFailed)
                    return new NormalizeResult(NormalizeStatus.Skipped, original,
                        "ffmpeg failed during loudness measurement");

                var applyFilter = BuildApplyFilter(measure);

                // loudnorm resamples internally to 192 kHz — without explicit format args the
                // written WAV inherits that rate. Force canonical 24 kHz mono 16-bit PCM.
                var applyArgs = BuildApplyArgs(inputPath, applyFilter, CanonicalFormatArgs, outputPath);
                var applyResult = await RunApplyPassAsync(exe, applyArgs, ct);

                if (applyResult.outcome == ApplyOutcome.ExeNotFound)
                    return new NormalizeResult(NormalizeStatus.Skipped, original,
                        "ffmpeg not found (set path in Audio Processing settings)");

                if (applyResult.outcome == ApplyOutcome.FfmpegFailed)
                    return new NormalizeResult(NormalizeStatus.Skipped, original,
                        $"ffmpeg failed: {applyResult.stderr}");

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

        public async Task<Stream> NormalizeToWavAsync(Stream input, string? ffmpegPath, CancellationToken ct = default)
        {
            var exe = ResolveExe(ffmpegPath);

            // Input gets a .wav extension — ffmpeg sniffs container and ignores extension.
            var inputPath = TempPath("r2m-ref-in", "wav");
            var outputPath = TempPath("r2m-ref-out", "wav");

            try
            {
                var buffered = await ToRewoundMemoryStreamAsync(input, ct);
                await WriteToFileAsync(buffered, inputPath, ct);

                var (measureResult, measure) = await RunMeasurePassAsync(exe, inputPath, ct);

                if (measureResult == MeasureOutcome.ExeNotFound)
                    throw new InvalidOperationException(
                        "ffmpeg is required to process voice audio (set the path in Audio Processing settings).");

                // loudnorm measure failure → bare transcode fallback (still canonical WAV)
                bool useBareTranscode = measureResult == MeasureOutcome.FfmpegFailed || measure is null;

                if (!useBareTranscode)
                {
                    var applyFilter = BuildApplyFilter(measure);
                    var applyArgs = BuildApplyArgs(inputPath, applyFilter, CanonicalFormatArgs, outputPath);
                    var applyResult = await RunApplyPassAsync(exe, applyArgs, ct);

                    if (applyResult.outcome == ApplyOutcome.ExeNotFound)
                        throw new InvalidOperationException(
                            "ffmpeg is required to process voice audio (set the path in Audio Processing settings).");

                    if (applyResult.outcome == ApplyOutcome.FfmpegFailed)
                        useBareTranscode = true; // non-fatal apply failure → fall back
                }

                if (useBareTranscode)
                    await RunBareTranscodeAsync(exe, inputPath, outputPath, ct);

                return await ReadRewoundAsync(outputPath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                TryDelete(inputPath);
                TryDelete(outputPath);
            }
        }

        // Runs ffmpeg bare transcode (no loudnorm) to canonical WAV format.
        // Throws InvalidOperationException on absent exe or decode failure.
        private async Task RunBareTranscodeAsync(string exe, string inputPath, string outputPath, CancellationToken ct)
        {
            var stderr = new StringBuilder();
            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(new[] { "-y", "-i", inputPath }
                        .Concat(CanonicalFormatArgs)
                        .Append(outputPath))
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
                var trimmed = stderr.ToString().Trim();
                var msg = trimmed.Length > 500 ? trimmed[..500] : trimmed;
                throw new InvalidOperationException($"Could not process audio: {msg}");
            }
        }

        // ── Shared two-pass internals ──────────────────────────────────────

        private enum MeasureOutcome { Ok, ExeNotFound, FfmpegFailed }
        private enum ApplyOutcome { Ok, ExeNotFound, FfmpegFailed }

        private async Task<(MeasureOutcome outcome, LoudnormMeasure? measure)> RunMeasurePassAsync(
            string exe, string inputPath, CancellationToken ct)
        {
            var measureStderr = new StringBuilder();
            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(new[]
                    {
                        "-y", "-i", inputPath,
                        "-af", $"{LoudnormBase}:print_format=json",
                        "-f", "null", "-"
                    })
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(measureStderr))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync(ct);

                var measure = ParseLoudnormJson(measureStderr.ToString());
                if (measure is null)
                    _logger.LogDebug("loudnorm measure pass: JSON not parsed — falling back to single-pass");

                return (MeasureOutcome.Ok, measure);
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg executable '{Exe}' not found", exe);
                return (MeasureOutcome.ExeNotFound, null);
            }
            catch (CommandExecutionException ex)
            {
                _logger.LogWarning("ffmpeg measure pass exited with code {Code}", ex.ExitCode);
                return (MeasureOutcome.FfmpegFailed, null);
            }
        }

        private async Task<(ApplyOutcome outcome, string stderr)> RunApplyPassAsync(
            string exe, string[] args, CancellationToken ct)
        {
            var applyStderr = new StringBuilder();
            try
            {
                await Cli.Wrap(exe)
                    .WithArguments(args)
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(applyStderr))
                    .WithValidation(CommandResultValidation.ZeroExitCode)
                    .ExecuteAsync(ct);

                return (ApplyOutcome.Ok, string.Empty);
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg executable '{Exe}' not found", exe);
                return (ApplyOutcome.ExeNotFound, string.Empty);
            }
            catch (CommandExecutionException ex)
            {
                _logger.LogWarning("ffmpeg apply pass exited with code {Code}", ex.ExitCode);
                return (ApplyOutcome.FfmpegFailed, applyStderr.ToString().Trim());
            }
        }

        private static string BuildApplyFilter(LoudnormMeasure? measure) =>
            measure is not null
                ? $"{LoudnormBase}:measured_I={measure.InputI}:measured_TP={measure.InputTp}" +
                  $":measured_LRA={measure.InputLra}:measured_thresh={measure.InputThresh}" +
                  $":offset={measure.TargetOffset}:linear=true"
                : LoudnormBase;

        private static string[] BuildApplyArgs(
            string inputPath, string applyFilter, string[]? extraFormatArgs, string outputPath)
        {
            var args = new System.Collections.Generic.List<string>
            {
                "-y", "-i", inputPath,
                "-af", applyFilter
            };
            if (extraFormatArgs is not null)
                args.AddRange(extraFormatArgs);
            args.Add(outputPath);
            return args.ToArray();
        }

        // ── Pure JSON parser ───────────────────────────────────────────────

        /// <summary>
        /// Parses the five loudnorm measurement values from a raw ffmpeg stderr string.
        /// Locates the last <c>{ … }</c> JSON block in <paramref name="stderr"/> and extracts
        /// <c>input_i</c>, <c>input_tp</c>, <c>input_lra</c>, <c>input_thresh</c>, and
        /// <c>target_offset</c>. Returns <c>null</c> if the block is absent, malformed, or
        /// missing any required field.
        /// </summary>
        internal static LoudnormMeasure? ParseLoudnormJson(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
                return null;

            var start = stderr.LastIndexOf('{');
            var end = stderr.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;

            var json = stderr[start..(end + 1)];

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!TryGetString(root, "input_i", out var inputI) ||
                    !TryGetString(root, "input_tp", out var inputTp) ||
                    !TryGetString(root, "input_lra", out var inputLra) ||
                    !TryGetString(root, "input_thresh", out var inputThresh) ||
                    !TryGetString(root, "target_offset", out var targetOffset))
                    return null;

                return new LoudnormMeasure(inputI!, inputTp!, inputLra!, inputThresh!, targetOffset!);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static string ResolveExe(string? ffmpegPath) =>
            string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();

        private static string TempPath(string prefix, string ext) =>
            Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.{ext}");

        private static async Task WriteToFileAsync(Stream source, string path, CancellationToken ct)
        {
            source.Position = 0;
            await using var file = File.Create(path);
            await source.CopyToAsync(file, ct);
        }

        private static bool TryGetString(JsonElement element, string property, out string? value)
        {
            if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                return value is not null;
            }
            value = null;
            return false;
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
