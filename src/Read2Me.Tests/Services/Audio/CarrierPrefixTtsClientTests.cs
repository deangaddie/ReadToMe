using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.Transcription;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class CarrierPrefixTtsClientTests
    {
        private const int SampleRate = 16000;

        // ── fakes ────────────────────────────────────────────────────────────

        private sealed class FakeInnerTtsClient : IParagraphTtsClient
        {
            public int CallCount { get; private set; }
            public string? LastText { get; private set; }
            public byte[] WavBytes { get; set; } = [0x52, 0x49, 0x46, 0x46];

            public Task<Stream> GenerateAsync(string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, string? settingsOverrideJson,
                string? referenceTranscript = null, CancellationToken ct = default)
            {
                CallCount++;
                LastText = text;
                return Task.FromResult<Stream>(new MemoryStream(WavBytes, writable: false));
            }
        }

        private sealed class FakeTranscriptionClient : ITranscriptionClient
        {
            public IReadOnlyList<TranscribedWord> Words { get; set; } = [];
            public Exception? Throws { get; set; }
            public int CallCount { get; private set; }

            public Task<string> TranscribeAsync(TranscriptionServiceConfig config, Stream audio, string fileName,
                CancellationToken ct = default) => throw new NotSupportedException();

            public Task<IReadOnlyList<TranscribedWord>> TranscribeWithWordTimestampsAsync(
                TranscriptionServiceConfig config, Stream audio, string fileName,
                CancellationToken ct = default)
            {
                CallCount++;
                ct.ThrowIfCancellationRequested();
                if (Throws is not null) throw Throws;
                return Task.FromResult(Words);
            }
        }

        private sealed class FakeTranscriptionResolver(ITranscriptionClient client) : ITranscriptionClientResolver
        {
            public ITranscriptionClient Resolve(TranscriptionServiceType type) => client;
        }

        private sealed class FakeTranscriptionSettings(TranscriptionServiceConfig? config)
            : TranscriptionSettingsService(null!, NullLogger<TranscriptionSettingsService>.Instance)
        {
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult(config);
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static ParagraphTtsServiceConfig Config(bool enabled, int maxTargetChars = 30) => new()
        {
            Name = "VoxCpm2",
            Type = ParagraphTtsServiceType.VoxCpm2,
            SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended with
            {
                CarrierPrefixEnabled = enabled,
                CarrierMaxTargetChars = maxTargetChars,
            }),
        };

        private static (CarrierPrefixTtsClient Client, FakeInnerTtsClient Inner, FakeTranscriptionClient Transcription)
            Build(TranscriptionServiceConfig? transcriptionConfig = null)
        {
            var inner = new FakeInnerTtsClient();
            var transcription = new FakeTranscriptionClient();
            var client = new CarrierPrefixTtsClient(
                inner,
                new FakeTranscriptionResolver(transcription),
                new FakeTranscriptionSettings(transcriptionConfig ?? new TranscriptionServiceConfig()),
                NullLogger<CarrierPrefixTtsClient>.Instance);
            return (client, inner, transcription);
        }

        private static Task<Stream> GenerateAsync(
            CarrierPrefixTtsClient client, string text, ParagraphTtsServiceConfig config,
            string? referenceTranscript, CancellationToken ct = default) =>
            client.GenerateAsync(text, null, new MemoryStream(), config, null, referenceTranscript, ct);

        private static byte[] ToBytes(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>16 kHz mono 16-bit WAV: loud carrier 1.0 s, silence 0.2 s, loud target 0.3 s.</summary>
        private static byte[] CarrierTargetWav()
        {
            using var pcm = new MemoryStream();
            using var w = new BinaryWriter(pcm);
            void Run(short amplitude, double seconds)
            {
                for (int i = 0; i < (int)(SampleRate * seconds); i++)
                    w.Write(amplitude);
            }
            Run(20000, 1.0);
            Run(0, 0.2);
            Run(20000, 0.3);
            w.Flush();
            var data = pcm.ToArray();

            using var wav = new MemoryStream();
            using var h = new BinaryWriter(wav);
            h.Write("RIFF"u8);
            h.Write(4 + (8 + 16) + (8 + data.Length));
            h.Write("WAVE"u8);
            h.Write("fmt "u8);
            h.Write(16);
            h.Write((short)1);
            h.Write((short)1);
            h.Write(SampleRate);
            h.Write(SampleRate * 2);
            h.Write((short)2);
            h.Write((short)16);
            h.Write("data"u8);
            h.Write(data.Length);
            h.Write(data);
            h.Flush();
            return wav.ToArray();
        }

        private static IReadOnlyList<TranscribedWord> MatchingWords() =>
        [
            new("The", 0.0, 0.2),
            new("quick", 0.25, 0.5),
            new("brown", 0.55, 0.75),
            new("fox.", 0.8, 1.0),
            new("One.", 1.2, 1.5),
        ];

        private const string Transcript = "The quick brown fox.";

        // ── passthrough ──────────────────────────────────────────────────────

        [Fact]
        public async Task Disabled_PassesThroughUnchanged()
        {
            var (client, inner, transcription) = Build();
            inner.WavBytes = [1, 2, 3, 4];

            var result = await GenerateAsync(client, "1", Config(enabled: false), Transcript);

            Assert.Equal("1", inner.LastText);
            Assert.Equal(1, inner.CallCount);
            Assert.Equal(0, transcription.CallCount);
            Assert.Equal([1, 2, 3, 4], ToBytes(result));
        }

        [Fact]
        public async Task TextLongerThanThreshold_PassesThroughUnchanged()
        {
            var (client, inner, transcription) = Build();
            var text = new string('a', 31);

            await GenerateAsync(client, text, Config(enabled: true, maxTargetChars: 30), Transcript);

            Assert.Equal(text, inner.LastText);
            Assert.Equal(0, transcription.CallCount);
        }

        [Fact]
        public async Task BlankReferenceTranscript_PassesThroughUnchanged()
        {
            var (client, inner, transcription) = Build();

            await GenerateAsync(client, "1", Config(enabled: true), "   ");

            Assert.Equal("1", inner.LastText);
            Assert.Equal(0, transcription.CallCount);
        }

        [Fact]
        public async Task WhitespaceOnlyText_PassesThroughUnchanged()
        {
            var (client, inner, transcription) = Build();

            await GenerateAsync(client, "  ", Config(enabled: true), Transcript);

            Assert.Equal("  ", inner.LastText);
            Assert.Equal(0, transcription.CallCount);
        }

        // ── carrier text construction ────────────────────────────────────────

        [Fact]
        public async Task Enabled_InnerReceivesCarrierPlusTarget()
        {
            var (client, inner, transcription) = Build();
            transcription.Words = MatchingWords();
            inner.WavBytes = CarrierTargetWav();

            await GenerateAsync(client, " One. ", Config(enabled: true), Transcript);

            Assert.Equal("The quick brown fox. One.", inner.LastText);
            Assert.Equal(1, inner.CallCount);
        }

        [Fact]
        public async Task TranscriptWithoutTerminalPunctuation_GetsPeriodAppended()
        {
            var (client, inner, transcription) = Build();
            transcription.Words = MatchingWords();
            inner.WavBytes = CarrierTargetWav();

            await GenerateAsync(client, "One.", Config(enabled: true), "The quick brown fox");

            Assert.Equal("The quick brown fox. One.", inner.LastText);
        }

        // ── trimming ─────────────────────────────────────────────────────────

        [Fact]
        public async Task Enabled_TrimsCarrierAudio_KeepsTargetPortion()
        {
            var (client, inner, transcription) = Build();
            transcription.Words = MatchingWords();
            inner.WavBytes = CarrierTargetWav();

            var result = await GenerateAsync(client, "One.", Config(enabled: true), Transcript);

            var bytes = ToBytes(result);
            Assert.Equal(1, transcription.CallCount);
            Assert.True(bytes.Length < inner.WavBytes.Length);
            // Cut lands in the silent gap [1.0 s, 1.2 s]; remaining audio is 0.3–0.5 s
            // of the 1.5 s total (plus 44-byte header).
            int remainingPcm = bytes.Length - 44;
            Assert.InRange(remainingPcm, (int)(0.3 * SampleRate) * 2, (int)(0.5 * SampleRate) * 2);
        }

        // ── failure fallbacks ────────────────────────────────────────────────

        [Fact]
        public async Task TranscriptionThrows_ReturnsUntrimmedCombinedAudio()
        {
            var (client, inner, transcription) = Build();
            transcription.Throws = new InvalidOperationException("whisper down");
            inner.WavBytes = CarrierTargetWav();

            var result = await GenerateAsync(client, "One.", Config(enabled: true), Transcript);

            Assert.Equal(inner.WavBytes, ToBytes(result));
        }

        [Fact]
        public async Task AlignmentFails_ReturnsUntrimmedCombinedAudio()
        {
            var (client, inner, transcription) = Build();
            transcription.Words = [new("garbage", 0.0, 1.5)];
            inner.WavBytes = CarrierTargetWav();

            var result = await GenerateAsync(client, "One.", Config(enabled: true), Transcript);

            Assert.Equal(inner.WavBytes, ToBytes(result));
        }

        [Fact]
        public async Task NoTranscriptionConfig_ReturnsUntrimmedWithoutTranscribing()
        {
            var inner = new FakeInnerTtsClient { WavBytes = CarrierTargetWav() };
            var transcription = new FakeTranscriptionClient { Words = MatchingWords() };
            var client = new CarrierPrefixTtsClient(
                inner,
                new FakeTranscriptionResolver(transcription),
                new FakeTranscriptionSettings(null),
                NullLogger<CarrierPrefixTtsClient>.Instance);

            var result = await GenerateAsync(client, "One.", Config(enabled: true), Transcript);

            Assert.Equal(0, transcription.CallCount);
            Assert.Equal(inner.WavBytes, ToBytes(result));
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            var (client, inner, _) = Build();
            inner.WavBytes = CarrierTargetWav();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                GenerateAsync(client, "One.", Config(enabled: true), Transcript, cts.Token));
        }
    }
}
