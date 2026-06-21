using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
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

        // --- Fake settings service returning a fixed chunking config. ---
        private sealed class FakeSettings : AudioProcessingSettingsService
        {
            private readonly AudioProcessingSettings _settings;
            public FakeSettings(bool enabled, int pauseMs, int minChunkChars)
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance) =>
                _settings = new AudioProcessingSettings(null, 0.15, enabled, pauseMs, minChunkChars);

            public override Task<AudioProcessingSettings> GetAsync() => Task.FromResult(_settings);
        }

        private static SentenceChunkedTtsClient NewClient(
            IParagraphTtsClient inner, AudioProcessingSettingsService settings) =>
            new(inner, settings, NullLogger<SentenceChunkedTtsClient>.Instance);

        private static async Task<byte[]> Generate(SentenceChunkedTtsClient client, string text)
        {
            using var refAudio = new MemoryStream(new byte[] { 9, 9, 9 });
            using var result = await client.GenerateAsync(
                text, "instr", refAudio, new ParagraphTtsServiceConfig());
            using var ms = new MemoryStream();
            await result.CopyToAsync(ms);
            return ms.ToArray();
        }

        [Fact]
        public async Task ToggleOff_SingleInnerCall_OriginalText_ByteIdentical()
        {
            var innerWav = BuildWav(100, 0x11);
            var inner = new FakeInnerClient { WavFor = _ => innerWav };
            var client = NewClient(inner, new FakeSettings(enabled: false, pauseMs: 200, minChunkChars: 15));

            var text = "First sentence here. Second sentence here. Third sentence here.";
            var result = await Generate(client, text);

            Assert.Single(inner.Texts);
            Assert.Equal(text, inner.Texts[0]);
            Assert.Equal(innerWav, result);
        }

        [Fact]
        public async Task SingleSentence_OneInnerCall_ByteIdentical_RegardlessOfToggle()
        {
            var innerWav = BuildWav(100, 0x11);
            var inner = new FakeInnerClient { WavFor = _ => innerWav };
            var client = NewClient(inner, new FakeSettings(enabled: true, pauseMs: 200, minChunkChars: 15));

            var text = "This is a single complete sentence.";
            var result = await Generate(client, text);

            Assert.Single(inner.Texts);
            Assert.Equal(text, inner.Texts[0]);
            Assert.Equal(innerWav, result); // one chunk → passthrough, no silence
        }

        [Fact]
        public async Task ChunkThrows_DecoratorThrows_NoPartialAudio()
        {
            var inner = new FakeInnerClient
            {
                WavFor = _ => BuildWav(100, 0x11),
                ThrowOnTextContaining = "Birds",
            };
            var client = NewClient(inner, new FakeSettings(enabled: true, pauseMs: 200, minChunkChars: 15));

            var text = "The sun rose over the hills. Birds began to sing loudly. A new day had started.";

            await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(client, text));
        }

        [Fact]
        public async Task MultiSentence_OneInnerCallPerSentence_StitchedWithPause()
        {
            var inner = new FakeInnerClient { WavFor = _ => BuildWav(100, 0x11) };
            var client = NewClient(inner, new FakeSettings(enabled: true, pauseMs: 200, minChunkChars: 15));

            var text = "The sun rose over the hills. Birds began to sing loudly. A new day had started.";
            var result = await Generate(client, text);

            Assert.Equal(3, inner.Texts.Count);
            Assert.Equal("The sun rose over the hills.", inner.Texts[0]);
            Assert.Equal("Birds began to sing loudly.", inner.Texts[1]);
            Assert.Equal("A new day had started.", inner.Texts[2]);

            int blockAlign = Channels * BitsPerSample / 8;
            int pcmPerChunk = 100 * blockAlign;
            int silenceBytes = (int)((long)SampleRate * 200 / 1000) * blockAlign;
            Assert.Equal(3 * pcmPerChunk + 2 * silenceBytes, ReadDataLength(result));
        }
    }
}
