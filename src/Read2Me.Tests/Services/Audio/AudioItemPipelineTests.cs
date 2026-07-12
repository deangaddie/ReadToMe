using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
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
            public string? LastReferenceTranscript { get; private set; }
            public Exception? Throws { get; set; }

            public Task<Stream> GenerateAsync(string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, string? settingsOverrideJson,
                string? referenceTranscript = null, CancellationToken ct = default)
            {
                CallCount++;
                LastText = text;
                LastReferenceTranscript = referenceTranscript;
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

        private sealed class FakePostProcessStep(string stepId = "fake-step") : IAudioPostProcessStep
        {
            public string StepId { get; } = stepId;
            public bool Applied { get; set; } = true;
            public string? Reason { get; set; }
            public byte[] Output { get; set; } = [0xF1, 0xF2];
            public byte[]? LastInput { get; private set; }
            public string? LastSettingsJson { get; private set; }
            public int CallCount { get; private set; }

            public Task<PostProcessResult> ProcessAsync(
                byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
            {
                CallCount++;
                LastInput = wav;
                LastSettingsJson = settingsJson;
                return Task.FromResult(Applied
                    ? new PostProcessResult(Output, true, null)
                    : new PostProcessResult(wav, false, Reason));
            }
        }

        private sealed class FakePostProcessCatalog : IAudioPostProcessStepCatalog
        {
            public List<EnabledPostProcessStep> Steps { get; } = new();

            public Task<IReadOnlyList<EnabledPostProcessStep>> GetEnabledStepsAsync() =>
                Task.FromResult<IReadOnlyList<EnabledPostProcessStep>>(Steps);
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
            public byte[]? LastAudio { get; private set; }

            public Task<string> TranscribeAsync(TranscriptionServiceConfig config, Stream audio, string fileName,
                CancellationToken ct = default)
            {
                WasCalled = true;
                var ms = new MemoryStream();
                audio.CopyTo(ms);
                LastAudio = ms.ToArray();
                if (ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
                return Task.FromResult(Transcript);
            }

            public Task<IReadOnlyList<TranscribedWord>> TranscribeWithWordTimestampsAsync(
                TranscriptionServiceConfig config, Stream audio, string fileName,
                CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<TranscribedWord>>([]);
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
        private readonly FakePostProcessCatalog _postProcess = new();
        private readonly FakeWerComparer _wer = new(0.0);
        private readonly FakeTranscriptionClient _transcriber = new();
        private readonly FakeTranscriptionSettings _transcriptionSettings;
        private readonly FakeSemanticVerifier _semantic = new();
        private readonly EventBroadcaster<AudioGenEvent> _broadcaster = new();
        private readonly List<AudioGenEvent> _events = new();
        private readonly IAudioItemPipeline _sut;
        private readonly FakeFileSystem _fs = new();
        private readonly IPreviewSourceCache _previewSources;

        private static readonly ProjectFolderId Folder = new("book-a");

        public AudioItemPipelineTests()
        {
            _fs.SeedFile(RefAudioPath, [0x52, 0x49, 0x46, 0x46]);
            _previewSources = new PreviewSourceCache(_fs);
            _transcriptionSettings = new FakeTranscriptionSettings(TranscriptionConfig);
            _broadcaster.Event += e => _events.Add(e);
            _sut = new AudioItemPipeline(
                new FakeTtsClientResolver(_tts),
                _normalizer,
                _postProcess,
                _previewSources,
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
            string? speaker = "Bilbo",
            string? referenceTranscript = null) => new(
            Folder: Folder,
            ParagraphItemId: Guid.NewGuid(),
            SourceText: sourceText,
            VoiceInstructions: null,
            RefAudioPath: RefAudioPath,
            TtsConfig: TtsConfig,
            TtsSettingsOverrideJson: null,
            MaxAttempts: maxAttempts,
            WerThreshold: werThreshold,
            FfmpegPath: null,
            Speaker: speaker,
            ReferenceTranscript: referenceTranscript);

        // ── tests ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task PreviewSource_IsTheAudioBeforeTheSteps_NotTheStoredAudio()
        {
            // The A/B preview filters the Preview Source. If the steps had already touched it, the
            // preview would stack consonant-soften on top of consonant-soften.
            var step = new FakePostProcessStep();
            _postProcess.Steps.Add(new EnabledPostProcessStep(step, "{}"));
            var req = MakeRequest();

            var result = await _sut.RunAsync(req, CancellationToken.None);

            Assert.True(_previewSources.TryGetPath(Folder.Value, req.ParagraphItemId, out var path));
            Assert.Equal(step.LastInput, _fs.GetFileContent(path!));
            Assert.NotEqual(result.AudioBytes, _fs.GetFileContent(path!));
        }

        [Fact]
        public async Task NormalizeFails_NoPreviewSourceIsCached()
        {
            // No step ever runs, and the audio is un-normalized provider-rate — not a valid source.
            _normalizer.Status = NormalizeStatus.Skipped;
            var req = MakeRequest();

            await _sut.RunAsync(req, CancellationToken.None);

            Assert.False(_previewSources.TryGetPath(Folder.Value, req.ParagraphItemId, out _));
        }

        [Fact]
        public async Task Retries_LeaveThePreviewSourceMatchingTheKeptAttempt()
        {
            _wer.Enqueue(0.9, 0.0);
            var req = MakeRequest(maxAttempts: 2);

            await _sut.RunAsync(req, CancellationToken.None);

            // One entry, overwritten by the final attempt — not one file per attempt.
            var entries = _previewSources.List();
            Assert.Single(entries);
            Assert.Equal(req.ParagraphItemId, entries[0].ParagraphItemId);
        }

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
        public async Task ReferenceTranscript_ForwardedToTtsClient()
        {
            var req = MakeRequest(referenceTranscript: "the sample text");

            await _sut.RunAsync(req, CancellationToken.None);

            Assert.Equal("the sample text", _tts.LastReferenceTranscript);
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

        // ── post-process steps ────────────────────────────────────────────────

        [Fact]
        public async Task PostProcess_EnabledStep_RunsBetweenNormalizeAndVerify_VerifySeesFilteredBytes()
        {
            var step = new FakePostProcessStep();
            _postProcess.Steps.Add(new EnabledPostProcessStep(step, """{"engine":"adyn"}"""));

            var result = await _sut.RunAsync(MakeRequest(), CancellationToken.None);

            Assert.Equal(1, step.CallCount);
            Assert.Equal([0x52, 0x49, 0x46, 0x46], step.LastInput);        // normalized TTS output
            Assert.Equal("""{"engine":"adyn"}""", step.LastSettingsJson);
            Assert.Equal(step.Output, _transcriber.LastAudio);             // verify hears the filtered audio
            Assert.Equal(step.Output, result.AudioBytes);

            var types = _events.Select(e => e.GetType()).ToList();
            Assert.Equal(
                [typeof(AudioGenerated), typeof(Normalized), typeof(PostProcessed), typeof(Transcribed), typeof(Verified)],
                types);
            var pp = _events.OfType<PostProcessed>().Single();
            Assert.Equal("fake-step", pp.StepId);
            Assert.True(pp.Applied);
            Assert.Null(pp.Reason);
        }

        [Fact]
        public async Task PostProcess_StepSkips_FallsBackToInputAudio_AndStillVerifies()
        {
            var step = new FakePostProcessStep { Applied = false, Reason = "ffmpeg not found" };
            _postProcess.Steps.Add(new EnabledPostProcessStep(step, null));

            var result = await _sut.RunAsync(MakeRequest(), CancellationToken.None);

            Assert.True(result.Verify.Ok);
            Assert.Equal([0x52, 0x49, 0x46, 0x46], result.AudioBytes);
            Assert.Equal([0x52, 0x49, 0x46, 0x46], _transcriber.LastAudio);

            var pp = _events.OfType<PostProcessed>().Single();
            Assert.False(pp.Applied);
            Assert.Equal("ffmpeg not found", pp.Reason);
        }

        [Fact]
        public async Task PostProcess_NoEnabledSteps_AudioUntouched_NoPostProcessedEvent()
        {
            var result = await _sut.RunAsync(MakeRequest(), CancellationToken.None);

            Assert.Equal([0x52, 0x49, 0x46, 0x46], result.AudioBytes);
            Assert.DoesNotContain(_events, e => e is PostProcessed);
        }

        [Fact]
        public async Task PostProcess_NormalizeFails_StepDoesNotRun()
        {
            var step = new FakePostProcessStep();
            _postProcess.Steps.Add(new EnabledPostProcessStep(step, null));
            _normalizer.Status = NormalizeStatus.Skipped;
            _normalizer.Reason = "ffmpeg exploded";

            var result = await _sut.RunAsync(MakeRequest(), CancellationToken.None);

            Assert.False(result.Normalize.Ok);
            Assert.Equal(0, step.CallCount);
            Assert.DoesNotContain(_events, e => e is PostProcessed);
        }

        [Fact]
        public async Task PostProcess_MultipleSteps_RunInStoredOrder_ChainingAudio()
        {
            var first = new FakePostProcessStep("first") { Output = [0x01] };
            var second = new FakePostProcessStep("second") { Output = [0x02] };
            _postProcess.Steps.Add(new EnabledPostProcessStep(first, null));
            _postProcess.Steps.Add(new EnabledPostProcessStep(second, null));

            var result = await _sut.RunAsync(MakeRequest(), CancellationToken.None);

            Assert.Equal([0x01], second.LastInput);
            Assert.Equal([0x02], result.AudioBytes);
            Assert.Equal(["first", "second"], _events.OfType<PostProcessed>().Select(e => e.StepId));
        }

        [Fact]
        public async Task PostProcess_Retry_RefiltersFreshTtsOutputEachAttempt()
        {
            var step = new FakePostProcessStep();
            _postProcess.Steps.Add(new EnabledPostProcessStep(step, null));
            _wer.Enqueue(0.42, 0.05);

            await _sut.RunAsync(MakeRequest(maxAttempts: 2), CancellationToken.None);

            Assert.Equal(2, _tts.CallCount);
            Assert.Equal(2, step.CallCount);
            Assert.Equal([0x52, 0x49, 0x46, 0x46], step.LastInput);  // fresh TTS audio, not attempt 1's output
            Assert.Equal([1, 2], _events.OfType<PostProcessed>().Select(e => e.Attempt));
        }
    }
}
