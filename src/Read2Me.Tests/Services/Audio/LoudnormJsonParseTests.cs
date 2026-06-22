using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class LoudnormJsonParseTests
    {
        // Representative ffmpeg stderr: log lines surrounding the JSON block.
        private const string TypicalStderr = """
            ffmpeg version 7.1 Copyright (c) 2000-2024 the FFmpeg developers
            Input #0, wav, from 'input.wav':
              Duration: 00:00:03.00, bitrate: 768 kb/s
            Stream #0:0: Audio: pcm_s16le, 24000 Hz, mono, s16, 384 kb/s
            [Parsed_loudnorm_0 @ 0x...] Input Integrated:    -27.61 LUFS
            [Parsed_loudnorm_0 @ 0x...] Input True Peak:      -9.72 dBTP
            {
                "input_i" : "-27.61",
                "input_tp" : "-9.72",
                "input_lra" : "5.40",
                "input_thresh" : "-37.88",
                "output_i" : "-15.99",
                "output_tp" : "-1.50",
                "output_lra" : "4.90",
                "output_thresh" : "-26.21",
                "normalization_type" : "dynamic",
                "target_offset" : "-0.01"
            }
            """;

        [Fact]
        public void ParsesRepresentativeSterr_IntoFiveMeasuredValues()
        {
            var result = FfmpegAudioNormalizer.ParseLoudnormJson(TypicalStderr);

            Assert.NotNull(result);
            Assert.Equal("-27.61", result.InputI);
            Assert.Equal("-9.72", result.InputTp);
            Assert.Equal("5.40", result.InputLra);
            Assert.Equal("-37.88", result.InputThresh);
            Assert.Equal("-0.01", result.TargetOffset);
        }

        [Fact]
        public void EmptyString_ReturnsNull()
        {
            Assert.Null(FfmpegAudioNormalizer.ParseLoudnormJson(""));
        }

        [Fact]
        public void StderrWithNoJsonBlock_ReturnsNull()
        {
            const string noJson = """
                ffmpeg version 7.1
                Input #0, wav, from 'input.wav':
                Some log output but no JSON block here.
                """;

            Assert.Null(FfmpegAudioNormalizer.ParseLoudnormJson(noJson));
        }

        [Fact]
        public void JsonMissingRequiredField_ReturnsNull()
        {
            // Missing input_i
            const string missingField = """
                {
                    "input_tp" : "-9.72",
                    "input_lra" : "5.40",
                    "input_thresh" : "-37.88",
                    "target_offset" : "-0.01"
                }
                """;

            Assert.Null(FfmpegAudioNormalizer.ParseLoudnormJson(missingField));
        }

        [Fact]
        public void MalformedJson_ReturnsNull()
        {
            const string malformed = """{ "input_i": """;

            Assert.Null(FfmpegAudioNormalizer.ParseLoudnormJson(malformed));
        }
    }
}
