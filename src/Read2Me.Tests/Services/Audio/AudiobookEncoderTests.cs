using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    // ── ConcatListBuilder ─────────────────────────────────────────────────────

    public class ConcatListBuilderTests
    {
        [Fact]
        public void Build_SinglePath_ProducesCorrectLine()
        {
            var result = ConcatListBuilder.Build(new[] { "/tmp/a.wav" });

            Assert.Equal("file '/tmp/a.wav'\n", result, ignoreLineEndingDifferences: true);
        }

        [Fact]
        public void Build_MultiplePaths_EachOnOwnLine()
        {
            var result = ConcatListBuilder.Build(new[] { "/tmp/a.wav", "/tmp/b.wav" });

            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("file '/tmp/a.wav'", lines[0].TrimEnd('\r'));
            Assert.Equal("file '/tmp/b.wav'", lines[1].TrimEnd('\r'));
        }

        [Fact]
        public void Build_EmptySequence_ReturnsEmptyString()
        {
            var result = ConcatListBuilder.Build(Array.Empty<string>());

            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void EscapePath_NoSpecialChars_Unchanged()
        {
            Assert.Equal("/tmp/audio.wav", ConcatListBuilder.EscapePath("/tmp/audio.wav"));
        }

        [Fact]
        public void EscapePath_SingleQuote_EscapedAsShellSequence()
        {
            // ffmpeg concat demuxer: ' → '\''
            var result = ConcatListBuilder.EscapePath("/tmp/it's a file.wav");

            Assert.Equal("/tmp/it'\\''s a file.wav", result);
        }

        [Fact]
        public void EscapePath_MultipleSingleQuotes_AllEscaped()
        {
            var result = ConcatListBuilder.EscapePath("a'b'c");

            Assert.Equal("a'\\''b'\\''c", result);
        }

        [Fact]
        public void Build_PathWithSingleQuote_AppearsEscapedInOutput()
        {
            var result = ConcatListBuilder.Build(new[] { "/tmp/don't.wav" });

            Assert.Contains("'\\''", result);
            Assert.StartsWith("file '", result);
        }
    }

    // ── FfmpegProgressParser ──────────────────────────────────────────────────

    public class FfmpegProgressParserTests
    {
        private static readonly TimeSpan Total60s = TimeSpan.FromSeconds(60);

        [Fact]
        public void ParseProgress_TypicalLine_ReturnsFraction()
        {
            // 30 seconds elapsed out of 60 → 0.5
            var result = FfmpegProgressParser.ParseProgress(
                "frame=  100 fps= 25 q=-0.0 size=   256kB time=00:00:30.00 bitrate=  69.9kbits/s",
                Total60s);

            Assert.NotNull(result);
            Assert.Equal(0.5, result!.Value, precision: 4);
        }

        [Fact]
        public void ParseProgress_AtStart_ReturnsZero()
        {
            var result = FfmpegProgressParser.ParseProgress(
                "time=00:00:00.00 bitrate=N/A", Total60s);

            Assert.NotNull(result);
            Assert.Equal(0.0, result!.Value, precision: 4);
        }

        [Fact]
        public void ParseProgress_BeyondTotal_ClampedToOne()
        {
            // elapsed > total → clamp to 1.0
            var result = FfmpegProgressParser.ParseProgress(
                "time=00:01:30.00 bitrate=...", Total60s);

            Assert.NotNull(result);
            Assert.Equal(1.0, result!.Value, precision: 4);
        }

        [Fact]
        public void ParseProgress_NoTimePattern_ReturnsNull()
        {
            var result = FfmpegProgressParser.ParseProgress(
                "frame=  0 fps=0.0 q=0.0 Lsize=    0kB", Total60s);

            Assert.Null(result);
        }

        [Fact]
        public void ParseProgress_EmptyLine_ReturnsNull()
        {
            Assert.Null(FfmpegProgressParser.ParseProgress(string.Empty, Total60s));
        }

        [Fact]
        public void ParseProgress_ZeroTotalDuration_ReturnsNull()
        {
            var result = FfmpegProgressParser.ParseProgress(
                "time=00:00:05.00 bitrate=...", TimeSpan.Zero);

            Assert.Null(result);
        }

        [Fact]
        public void ParseProgress_HoursIncluded_ParsedCorrectly()
        {
            // 1 hour 30 minutes = 5400 seconds; total = 2 hours = 7200 seconds → 0.75
            var total = TimeSpan.FromHours(2);
            var result = FfmpegProgressParser.ParseProgress(
                "time=01:30:00.00 bitrate=...", total);

            Assert.NotNull(result);
            Assert.Equal(0.75, result!.Value, precision: 4);
        }

        [Fact]
        public void ParseProgress_FractionalSeconds_ParsedCorrectly()
        {
            // 15.5s of 60s → ~0.2583
            var result = FfmpegProgressParser.ParseProgress(
                "time=00:00:15.50 bitrate=...", Total60s);

            Assert.NotNull(result);
            Assert.InRange(result!.Value, 0.258, 0.260);
        }
    }

    // ── AudiobookEncoder.BuildEncodeArgs (pure) ───────────────────────────────

    public class AudiobookEncoderBuildEncodeArgsTests
    {
        [Fact]
        public void BuildEncodeArgs_WithCover_IncludesCoverInputAndMap()
        {
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", "cover.jpg", "out.m4b");

            var list = args.ToList();
            // Cover input present
            Assert.Contains("cover.jpg", list);
            // Video map and mjpeg codec present
            Assert.Contains("-c:v", list);
            Assert.Contains("mjpeg", list);
            Assert.Contains("-disposition:v", list);
            Assert.Contains("attached_pic", list);
        }

        [Fact]
        public void BuildEncodeArgs_NoCover_OmitsVideoMapAndCoverInput()
        {
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", null, "out.m4b");

            var list = args.ToList();
            Assert.DoesNotContain("-c:v", list);
            Assert.DoesNotContain("mjpeg", list);
            Assert.DoesNotContain("-disposition:v", list);
        }

        [Fact]
        public void BuildEncodeArgs_AudioEncode_IsAacMono64k24k()
        {
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", null, "out.m4b").ToList();

            int idx = args.IndexOf("-c:a");
            Assert.True(idx >= 0, "missing -c:a");
            Assert.Equal("aac", args[idx + 1]);

            int bidx = args.IndexOf("-b:a");
            Assert.True(bidx >= 0, "missing -b:a");
            Assert.Equal("64k", args[bidx + 1]);

            Assert.Contains("-ac", args);
            Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
            Assert.Contains("-ar", args);
            Assert.Equal("24000", args[args.IndexOf("-ar") + 1]);
        }

        [Fact]
        public void BuildEncodeArgs_ConcatDemuxer_ArgsPresent()
        {
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", null, "out.m4b").ToList();

            Assert.Contains("-f", args);
            int fidx = args.IndexOf("-f");
            Assert.Equal("concat", args[fidx + 1]);
            Assert.Contains("-safe", args);
            Assert.Contains("0", args);
            Assert.Contains("concat.txt", args);
        }

        [Fact]
        public void BuildEncodeArgs_MapMetadata_Present()
        {
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", null, "out.m4b").ToList();

            Assert.Contains("-map_metadata", args);
            // value should be "1" (ffmetadata is input index 1)
            int idx = args.IndexOf("-map_metadata");
            Assert.Equal("1", args[idx + 1]);
        }

        [Fact]
        public void BuildEncodeArgs_OutputPath_IsLastArg()
        {
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", null, "out.m4b");

            Assert.Equal("out.m4b", args[^1]);
        }

        [Fact]
        public void BuildEncodeArgs_ContainerFormat_IsIpod()
        {
            // -f ipod required so ffmpeg can mux m4b/m4a even when output has a .tmp extension
            var args = AudiobookEncoder.BuildEncodeArgs(
                "concat.txt", "meta.txt", null, "out.m4b.tmp").ToList();

            // Find the last -f before the output path (not the concat demuxer -f)
            int outputIdx = args.IndexOf("out.m4b.tmp");
            var formatIdx = args.LastIndexOf("-f", outputIdx - 1);
            Assert.True(formatIdx >= 0, "missing -f before output path");
            Assert.Equal("ipod", args[formatIdx + 1]);
        }
    }

    // ── Integration tests (ffmpeg-gated) ─────────────────────────────────────

    /// <summary>
    /// Real-ffmpeg tests. Skipped when ffprobe / ffmpeg are not on PATH.
    /// Mirrors the pattern used by <c>FfmpegAudioNormalizerTests</c>.
    /// </summary>
    public class AudiobookEncoderIntegrationTests : IDisposable
    {
        private readonly List<string> _tempFiles = new();

        private AudiobookEncoder NewEncoder() =>
            new(NullLogger<AudiobookEncoder>.Instance);

        private static bool FfmpegAvailable()
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        private string Track(string path) { _tempFiles.Add(path); return path; }

        private static string WriteTinyWav(int durationMs)
        {
            // Generates a minimal 24 kHz / mono / 16-bit PCM WAV via raw byte construction.
            int sampleRate = 24000;
            int channels = 1;
            int bitsPerSample = 16;
            int numSamples = (int)(sampleRate * durationMs / 1000.0);
            int dataBytes = numSamples * channels * (bitsPerSample / 8);

            var path = Path.Combine(Path.GetTempPath(), $"r2m-test-{Guid.NewGuid():N}.wav");
            using var bw = new BinaryWriter(File.Create(path));

            // RIFF header
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataBytes);       // ChunkSize
            bw.Write(new[] { 'W', 'A', 'V', 'E' });

            // fmt  sub-chunk
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);                   // Subchunk1Size (PCM)
            bw.Write((short)1);             // AudioFormat (PCM)
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * channels * (bitsPerSample / 8)); // ByteRate
            bw.Write((short)(channels * (bitsPerSample / 8)));     // BlockAlign
            bw.Write((short)bitsPerSample);

            // data sub-chunk
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataBytes);
            bw.Write(new byte[dataBytes]); // silence (all zeros)

            return path;
        }

        [Fact]
        public async Task GetDurationAsync_RealWav_ReturnsApproximateDuration()
        {
            if (!FfmpegAvailable())
                return; // skip

            var wav = Track(WriteTinyWav(durationMs: 500));
            var encoder = NewEncoder();

            var dur = await encoder.GetDurationAsync(wav, null);

            Assert.InRange(dur.TotalMilliseconds, 490, 510);
        }

        [Fact]
        public async Task GetSilenceAsync_ProducesCanonicalWav_CorrectDuration()
        {
            if (!FfmpegAvailable())
                return;

            var encoder = NewEncoder();
            var path = await encoder.GetSilenceAsync(ms: 1000, ffmpegPath: null);
            Track(path);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);

            // Probe the generated file to confirm duration ≈ 1 s
            var dur = await encoder.GetDurationAsync(path, null);
            Assert.InRange(dur.TotalMilliseconds, 990, 1010);
        }

        [Fact]
        public async Task GetSilenceAsync_SameMs_ReturnsSamePathWithoutRegeneration()
        {
            if (!FfmpegAvailable())
                return;

            var encoder = NewEncoder();
            var path1 = await encoder.GetSilenceAsync(500, null);
            Track(path1);
            var path2 = await encoder.GetSilenceAsync(500, null);

            Assert.Equal(path1, path2);
        }

        [Fact]
        public async Task EncodeAsync_ProducesM4bWithExpectedDuration()
        {
            if (!FfmpegAvailable())
                return;

            var encoder = NewEncoder();

            // Two 1-second WAVs + 500 ms silence between them → ~2.5 s total
            var wav1 = Track(WriteTinyWav(1000));
            var wav2 = Track(WriteTinyWav(1000));
            var silencePath = Track(await encoder.GetSilenceAsync(500, null));

            // Build concat list file
            var concatText = ConcatListBuilder.Build(new[] { wav1, silencePath, wav2 });
            var concatPath = Track(Path.Combine(Path.GetTempPath(), $"r2m-test-{Guid.NewGuid():N}.txt"));
            await File.WriteAllTextAsync(concatPath, concatText);

            // Build ffmetadata file
            var chapter = new ChapterMarker("Test Chapter", TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
            var ffmeta = AudiobookAssemblyPlanner.GenerateFfmetadata("Test Book", "Test Author",
                new List<ChapterMarker> { chapter });
            var metaPath = Track(Path.Combine(Path.GetTempPath(), $"r2m-test-{Guid.NewGuid():N}.txt"));
            await File.WriteAllTextAsync(metaPath, ffmeta);

            var outputPath = Track(Path.Combine(Path.GetTempPath(), $"r2m-test-{Guid.NewGuid():N}.m4b"));

            var totalDuration = TimeSpan.FromSeconds(2.5);
            var progressValues = new List<double>();

            await encoder.EncodeAsync(
                concatPath, metaPath, null, outputPath,
                totalDuration,
                new Progress<double>(v => progressValues.Add(v)),
                null);

            // File exists and is non-empty
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);

            // Progress reached 1.0
            Assert.Contains(1.0, progressValues);
        }

        [Fact]
        public async Task GetDurationAsync_AbsentFfprobe_ThrowsSettingsGuidanceMessage()
        {
            var bogus = Path.Combine(Path.GetTempPath(), $"no-ffmpeg-{Guid.NewGuid():N}.exe");
            var encoder = NewEncoder();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                encoder.GetDurationAsync("any.wav", bogus));

            Assert.Contains("Audio Processing settings", ex.Message);
        }

        [Fact]
        public async Task GetSilenceAsync_AbsentFfmpeg_ThrowsSettingsGuidanceMessage()
        {
            var bogus = Path.Combine(Path.GetTempPath(), $"no-ffmpeg-{Guid.NewGuid():N}.exe");
            var encoder = NewEncoder();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                encoder.GetSilenceAsync(500, bogus));

            Assert.Contains("Audio Processing settings", ex.Message);
        }

        [Fact]
        public async Task EncodeAsync_AbsentFfmpeg_ThrowsSettingsGuidanceMessage()
        {
            var bogus = Path.Combine(Path.GetTempPath(), $"no-ffmpeg-{Guid.NewGuid():N}.exe");
            var encoder = NewEncoder();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                encoder.EncodeAsync("c.txt", "m.txt", null, "out.m4b",
                    TimeSpan.FromSeconds(5), null, bogus));

            Assert.Contains("Audio Processing settings", ex.Message);
        }

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }
}
