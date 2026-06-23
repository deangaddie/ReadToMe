using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class SentenceChunkedTtsClientTests
    {
        // --- Synthetic WAV (16-bit PCM mono 16k) so the stitcher can parse fmt/data. ---
        private const int SampleRate = 16000;
        private const short Channels = 1;
        private const short BitsPerSample = 16;

        private static byte[] BuildWav(int sampleCount, byte fill)
        {
            short blockAlign = (short)(Channels * BitsPerSample / 8);
            int byteRate = SampleRate * blockAlign;
            var pcm = new byte[sampleCount * blockAlign];
            Array.Fill(pcm, fill);

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(4 + (8 + 16) + (8 + pcm.Length));
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write(Channels);
            w.Write(SampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(BitsPerSample);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);
            w.Flush();
            return ms.ToArray();
        }

        private static int ReadDataLength(byte[] wav)
        {
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int size = BitConverter.ToInt32(wav, pos + 4);
                if (id == "data") return size;
                pos += 8 + size + (size & 1);
            }
            throw new InvalidOperationException("no data");
        }

        // --- Fake inner client: records call texts, returns a fixed WAV per call. ---
        private sealed class FakeInnerClient : IParagraphTtsClient
        {
            public readonly List<string> Texts = new();
            public Func<string, byte[]> WavFor = _ => BuildWav(100, 0x11);
            public string? ThrowOnTextContaining;

            public Task<Stream> GenerateAsync(
                string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, CancellationToken ct = default)
            {
                Texts.Add(text);
                if (ThrowOnTextContaining != null && text.Contains(ThrowOnTextContaining))
                    throw new InvalidOperationException("synth failed");
                return Task.FromResult<Stream>(new MemoryStream(WavFor(text)));
            }
        }

        // --- Fake settings service: only ChunkPauseMs matters for chunking path. ---
        private sealed class FakeSettings : AudioProcessingSettingsService
        {
            private readonly AudioProcessingSettings _settings;
            public FakeSettings(int pauseMs)
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance) =>
                _settings = new AudioProcessingSettings(null, 0.15, SentenceSplitEnabled: false,
                    ChunkPauseMs: pauseMs, VolumePauseMs: 4000, PartPauseMs: 3000,
                    ChapterPauseMs: 2500, ParagraphPauseMs: 800, PauseMs: 500);

            public override Task<AudioProcessingSettings> GetAsync() => Task.FromResult(_settings);
        }

        private static ParagraphTtsServiceConfig ConfigWithMaxChunkChars(int maxChunkChars) =>
            new() { SettingsJson = JsonSerializer.Serialize(new VoxCpm2ParagraphTtsSettings { MaxChunkChars = maxChunkChars }) };

        private static SentenceChunkedTtsClient NewClient(
            IParagraphTtsClient inner, AudioProcessingSettingsService settings) =>
            new(inner, settings, NullLogger<SentenceChunkedTtsClient>.Instance);

        private static async Task<byte[]> Generate(
            SentenceChunkedTtsClient client, string text, ParagraphTtsServiceConfig? config = null)
        {
            using var refAudio = new MemoryStream(new byte[] { 9, 9, 9 });
            using var result = await client.GenerateAsync(
                text, "instr", refAudio, config ?? new ParagraphTtsServiceConfig());
            using var ms = new MemoryStream();
            await result.CopyToAsync(ms);
            return ms.ToArray();
        }

        [Fact]
        public async Task ShortParagraph_UnderCap_OneInnerCall_ByteIdentical()
        {
            // Multi-sentence paragraph whose total length fits in one chunk.
            var innerWav = BuildWav(100, 0x11);
            var inner = new FakeInnerClient { WavFor = _ => innerWav };
            var client = NewClient(inner, new FakeSettings(pauseMs: 200));

            var text = "The sun rose. Birds sang."; // 25 chars, well under any reasonable cap
            var config = ConfigWithMaxChunkChars(500);
            var result = await Generate(client, text, config);

            Assert.Single(inner.Texts);
            // Chunker packs all sentences into one chunk -> joined text
            Assert.Equal("The sun rose. Birds sang.", inner.Texts[0]);
            Assert.Equal(innerWav, result);
        }

        [Fact]
        public async Task LongParagraph_SplitsIntoChunks_StitchedWithChunkPauseMs()
        {
            var inner = new FakeInnerClient { WavFor = _ => BuildWav(100, 0x11) };
            var client = NewClient(inner, new FakeSettings(pauseMs: 200));

            // Each sentence ~29 chars. Cap=40 forces each sentence into its own chunk.
            var s1 = "The sun rose over the hills today.";   // 34
            var s2 = "Birds began to sing so very loudly."; // 35
            var s3 = "A bright new day had finally started."; // 37
            var text = $"{s1} {s2} {s3}";
            var config = ConfigWithMaxChunkChars(40);

            var result = await Generate(client, text, config);

            Assert.Equal(3, inner.Texts.Count);
            Assert.Equal(s1, inner.Texts[0]);
            Assert.Equal(s2, inner.Texts[1]);
            Assert.Equal(s3, inner.Texts[2]);

            int blockAlign = Channels * BitsPerSample / 8;
            int pcmPerChunk = 100 * blockAlign;
            int silenceBytes = (int)((long)SampleRate * 200 / 1000) * blockAlign;
            Assert.Equal(3 * pcmPerChunk + 2 * silenceBytes, ReadDataLength(result));
        }

        [Fact]
        public async Task ChunkThrows_DecoratorThrows_NoPartialAudio()
        {
            var inner = new FakeInnerClient
            {
                WavFor = _ => BuildWav(100, 0x11),
                ThrowOnTextContaining = "Birds",
            };
            var client = NewClient(inner, new FakeSettings(pauseMs: 200));

            // Cap=40 forces each sentence into its own chunk; "Birds" chunk throws.
            var text = "The sun rose over the hills today. Birds began to sing so very loudly. A bright new day had finally started.";
            var config = ConfigWithMaxChunkChars(40);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(client, text, config));
        }

        [Fact]
        public async Task SingleOversizedSentence_OneInnerCall_ByteIdentical()
        {
            // Sentence longer than MaxChunkChars; chunker returns it as a single chunk.
            var innerWav = BuildWav(100, 0x11);
            var inner = new FakeInnerClient { WavFor = _ => innerWav };
            var client = NewClient(inner, new FakeSettings(pauseMs: 200));

            var text = "This is a very long single sentence that exceeds the tiny chunk cap.";
            var config = ConfigWithMaxChunkChars(10); // cap smaller than the sentence

            var result = await Generate(client, text, config);

            Assert.Single(inner.Texts);
            Assert.Equal(text, inner.Texts[0]);
            Assert.Equal(innerWav, result);
        }
    }
}
