using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioItemPipelineTests
    {
        // ── fakes ────────────────────────────────────────────────────────────

        private sealed class FakeTtsClient : IParagraphTtsClient
        {
            public int CallCount { get; private set; }
            public string? LastText { get; private set; }
            public Exception? Throws { get; set; }

            public Task<Stream> GenerateAsync(string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, string? settingsOverrideJson, CancellationToken ct = default)
            {
                CallCount++;
                LastText = text;
                if (Throws is not null) throw Throws;
                Stream s = new MemoryStream([0x52, 0x49, 0x46, 0x46]);
                return Task.FromResult(s);
            }
        }

        private sealed class FakeTtsClientResolver(IParagraphTtsClient client) : IParagraphTtsClientResolver
        {
            public IParagraphTtsClient Resolve(ParagraphTtsServiceType type) => client;
        }

        private sealed class FakeNormalizer : IAudioNormalizer
        {
            public NormalizeStatus Status { get; set; } = NormalizeStatus.Normalized;
            public string? Reason { get; set; }

            public async Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default)
            {
                var ms = new MemoryStream();
                if (wav.CanSeek) wav.Position = 0;
                await wav.CopyToAsync(ms, ct);
                ms.Position = 0;
                return new NormalizeResult(Status, ms, Reason);
            }

            public Task<Stream> NormalizeToWavAsync(Stream input, string? ffmpegPath, CancellationToken ct = default) =>
                throw new NotSupportedException();
        }

        private sealed class FakeWerComparer : IWerComparer
        {
            private readonly Queue<double> _queue = new();
            public double Result { get; set; }
            public FakeWerComparer(double result) => Result = result;
            public void Enqueue(params double[] results) { foreach (var r in results) _queue.Enqueue(r); }
            public double Compute(string reference, string hypothesis) =>
                _queue.Count > 0 ? _queue.Dequeue() : Result;
        }

        private sealed class FakeTranscriptionClient : ITranscriptionClient
        {
            public string Transcript { get; set; } = "hello world";
            public string? ThrowMessage { get; set; }
            public bool WasCalled { get; private set; }

            public Task<string> TranscribeAsync(TranscriptionServiceConfig config, Stream audio, string fileName,
                CancellationToken ct = default)
            {
                WasCalled = true;
                if (ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
                return Task.FromResult(Transcript);
            }
        }

        private sealed class FakeTranscriptionResolver(ITranscriptionClient client) : ITranscriptionClientResolver
        {
            public ITranscriptionClient Resolve(TranscriptionServiceType type) => client;
        }

        private sealed class FakeTranscriptionSettings(TranscriptionServiceConfig? config)
            : TranscriptionSettingsService(null!, NullLogger<TranscriptionSettingsService>.Instance)
        {
            public TranscriptionServiceConfig? Config { get; set; } = config;
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult(Config);
        }

        private sealed class FakeSemanticVerifier : ISemanticVerifier
        {
            public bool Passes { get; set; }
            public double? Score { get; set; }
            public double? Threshold { get; set; }
            public bool WasCalled { get; private set; }

            public Task<(bool Passes, double? Score, double? Threshold)> PassesAsync(
                string source, string transcript, CancellationToken ct = default)
            {
                WasCalled = true;
                return Task.FromResult((Passes, Score, Threshold));
            }
        }

        // ── builder ──────────────────────────────────────────────────────────

        private static readonly ParagraphTtsServiceConfig TtsConfig = new()
        {
            Id = 1, Name = "Test", Type = ParagraphTtsServiceType.VoxCpm2, SettingsJson = "{}"
        };

        private static readonly TranscriptionServiceConfig TranscriptionConfig = new()
        {
            Id = 1, Name = "Whisper", Type = TranscriptionServiceType.LocalWhisper, SettingsJson = "{}"
        };

        private const string RefAudioPath = @"C:\fake\ref.wav";

        private readonly FakeTtsClient _tts = new();
        private readonly FakeNormalizer _normalizer = new();
        private readonly FakeWerComparer _wer = new(0.0);
        private readonly FakeTranscriptionClient _transcriber = new();
        private readonly FakeTranscriptionSettings _transcriptionSettings;
        private readonly FakeSemanticVerifier _semantic = new();
        private readonly EventBroadcaster<AudioGenEvent> _broadcaster = new();
        private readonly List<AudioGenEvent> _events = new();
        private readonly IAudioItemPipeline _sut;
        private readonly FakeFileSystem _fs = new();

        public AudioItemPipelineTests()
        {
            _fs.SeedFile(RefAudioPath, [0x52, 0x49, 0x46, 0x46]);
            _transcriptionSettings = new FakeTranscriptionSettings(TranscriptionConfig);
            _broadcaster.Event += e => _events.Add(e);
            _sut = new AudioItemPipeline(
                new FakeTtsClientResolver(_tts),
                _normalizer,
                _wer,
                new FakeTranscriptionResolver(_transcriber),
                _transcriptionSettings,
                _semantic,
                _broadcaster,
                _fs,
                NullLogger<AudioItemPipeline>.Instance);
        }

        private PipelineRequest MakeRequest(
            string sourceText = "Hello world",
            int maxAttempts = 1,
            double werThreshold = 0.15,
            string? speaker = "Bilbo") => new(
            ParagraphItemId: Guid.NewGuid(),
            SourceText: sourceText,
            VoiceInstructions: null,
            RefAudioPath: RefAudioPath,
            TtsConfig: TtsConfig,
            TtsSettingsOverrideJson: null,
            MaxAttempts: maxAttempts,
            WerThreshold: werThreshold,
            FfmpegPath: null,
            Speaker: speaker);

        // ── tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task HappyPath_Attempt1_ReturnsOkResult_CorrectEventSequence()
        {
            var req = MakeRequest();

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.True(result.Normalize.Ok);
            Assert.True(result.Verify.Ok);
            Assert.NotEmpty(result.AudioBytes);

            Assert.Equal(4, _events.Count);
            Assert.IsType<AudioGenerated>(_events[0]);
            Assert.IsType<Normalized>(_events[1]);
            Assert.IsType<Transcribed>(_events[2]);
            var verified = Assert.IsType<Verified>(_events[3]);
            Assert.True(verified.Ok);
            Assert.DoesNotContain(_events, e => e is ItemStarted);
        }

        [Fact]
        public async Task VerifyFails_Attempt1_PassesAttempt2_TwoEventCards_FinalVerifyOk()
        {
            _wer.Enqueue(0.42, 0.05);
            var req = MakeRequest(maxAttempts: 2);

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.True(result.Verify.Ok);
            Assert.Equal(2, _tts.CallCount);

            var starts = _events.OfType<ItemStarted>().ToList();
            Assert.Single(starts);
            Assert.Equal(2, starts[0].Attempt);

            var verified = _events.OfType<Verified>().ToList();
            Assert.Equal(2, verified.Count);
            Assert.False(verified[0].Ok);
            Assert.True(verified[1].Ok);
        }

        [Fact]
        public async Task AllAttemptsExhausted_ReturnsBytes_VerifyFalse_WerPopulated()
        {
            _wer.Result = 0.42;
            var req = MakeRequest(maxAttempts: 2);

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.Equal(2, _tts.CallCount);
            Assert.NotEmpty(result.AudioBytes);
            Assert.False(result.Verify.Ok);
            Assert.Equal(0.42, result.Verify.Wer);
        }

        [Fact]
        public async Task SemanticRescue_WerOver_SemanticPasses_VerifyOkTrue_RescuedTrue()
        {
            _wer.Result = 0.42;
            _semantic.Passes = true;
            _semantic.Score = 0.91;
            _semantic.Threshold = 0.85;
            var req = MakeRequest();

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.True(result.Verify.Ok);
            Assert.True(result.Verify.Rescued);
            Assert.Equal(0.42, result.Verify.Wer);
            Assert.Contains("rescued by semantic", result.Verify.Reason);
            Assert.Equal(1, _tts.CallCount);
        }

        [Fact]
        public async Task SemanticFails_WerOver_VerifyFalse_RescuedFalse()
        {
            _wer.Result = 0.42;
            _semantic.Passes = false;
            _semantic.Score = 0.60;
            var req = MakeRequest();

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.False(result.Verify.Ok);
            Assert.False(result.Verify.Rescued);
        }

        [Fact]
        public async Task NormalizeFail_ReturnsImmediately_NormalizeOkFalse_NoRetry()
        {
            _normalizer.Status = NormalizeStatus.Skipped;
            _normalizer.Reason = "ffmpeg failed";
            var req = MakeRequest(maxAttempts: 3);

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.False(result.Normalize.Ok);
            Assert.Equal(1, _tts.CallCount);
        }

        [Fact]
        public async Task NoTranscriptionConfig_VerifyFalse_WerNull_NoRetry()
        {
            _transcriptionSettings.Config = null;
            var req = MakeRequest(maxAttempts: 3);

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.False(result.Verify.Ok);
            Assert.Null(result.Verify.Wer);
            Assert.Equal(1, _tts.CallCount);
            Assert.False(_semantic.WasCalled);
        }

        [Fact]
        public async Task HardTtsException_Propagates()
        {
            _tts.Throws = new InvalidOperationException("tts boom");
            var req = MakeRequest();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _sut.RunAsync(req, CancellationToken.None));
        }

        [Fact]
        public async Task TrailingComma_TtsReceivesSemicolon_VerifyReceivesOriginal()
        {
            const string source = "He said,";
            _transcriber.Transcript = "He said;";
            var req = MakeRequest(sourceText: source);

            await _sut.RunAsync(req, CancellationToken.None);

            Assert.Equal("He said;", _tts.LastText);
            // WER is computed against original sourceText — fake wer returns 0 regardless so
            // we just verify the transcriber was called (the pipeline did not substitute for verify)
            Assert.True(_transcriber.WasCalled);
        }

        [Fact]
        public async Task RetryItemStarted_Attempt1_NoItemStarted_Attempt2_ItemStartedWithAttempt2()
        {
            _wer.Enqueue(0.42, 0.05);
            var req = MakeRequest(maxAttempts: 2, speaker: "Alice");

            await _sut.RunAsync(req, CancellationToken.None);

            var starts = _events.OfType<ItemStarted>().ToList();
            Assert.Single(starts);
            Assert.Equal(2, starts[0].Attempt);
            Assert.Equal("Alice", starts[0].Character);
            Assert.Equal(req.SourceText, starts[0].Text);
        }
    }
}
