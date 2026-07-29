using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.Queueing;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// The audio queue's translation surface: how a failure inside the pipeline becomes the
    /// <see cref="WorkOutcome"/> the queue decides from. The seam is
    /// <see cref="IAudioItemPipeline.RunAsync"/> — no client converts, so this is the only place the
    /// mapping exists, and the mirror of <c>CharacterDispositionTests</c>' table on the LLM side.
    /// </summary>
    public class AudioDispositionTests
    {
        private const string RefAudioPath = @"C:\fake\ref.wav";

        private static readonly ProjectFolderId Folder = new("book-a");

        private static readonly ParagraphTtsServiceConfig TtsConfig = new()
        {
            Id = 1, Name = "Test", Type = ParagraphTtsServiceType.VoxCpm2, SettingsJson = "{}"
        };

        private sealed class ThrowingTtsClient(Exception ex) : IParagraphTtsClient
        {
            public Task<Stream> GenerateAsync(string text, string? voiceInstructions, Stream referenceAudioStream,
                ParagraphTtsServiceConfig settings, string? settingsOverrideJson,
                string? referenceTranscript = null, CancellationToken ct = default) => throw ex;
        }

        private sealed class StubResolver(IParagraphTtsClient client) : IParagraphTtsClientResolver
        {
            public IParagraphTtsClient Resolve(ParagraphTtsServiceType type) => client;
        }

        /// <summary>
        /// TTS is the pipeline's first AI call, so a throwing client aborts the run before anything
        /// downstream of it exists — normalize, post-process, transcribe and semantic verify are all
        /// unreachable here, and are passed as nulls to say so. The richer harness lives in
        /// <c>AudioItemPipelineTests</c>; this file only needs the edge.
        /// </summary>
        private static IAudioItemPipeline PipelineWhoseTtsThrows(Exception ex)
        {
            var fs = new FakeFileSystem();
            fs.SeedFile(RefAudioPath, [0x52, 0x49, 0x46, 0x46]);
            return new AudioItemPipeline(
                new StubResolver(new ThrowingTtsClient(ex)),
                normalizer: null!,
                postProcessCatalog: null!,
                previewSources: null!,
                werComparer: null!,
                transcriptionResolver: null!,
                transcriptionSettings: null!,
                semanticVerifier: null!,
                new EventBroadcaster<AudioGenEvent>(),
                fs,
                NullLogger<AudioItemPipeline>.Instance);
        }

        private static PipelineRequest Request() => new(
            Folder: Folder,
            ParagraphItemId: Guid.NewGuid(),
            SourceText: "Hello world",
            VoiceInstructions: null,
            RefAudioPath: RefAudioPath,
            TtsConfig: TtsConfig,
            TtsSettingsOverrideJson: null,
            MaxAttempts: 3,
            WerThreshold: 0.15,
            FfmpegPath: null,
            Speaker: "Bilbo");

        /// <summary>
        /// The row the whole slice exists for. A managed service the watchdog is already recovering
        /// arrives at the queue as a value, exactly as <c>AttributionStatus.ServiceUnavailable</c>
        /// does on the character side — the exception type is how the client's own
        /// <c>ReportFailure</c> answer travels up, because audio's base URL is client-private.
        /// </summary>
        [Fact]
        public async Task AiServiceUnavailable_IsUnavailable()
        {
            var ex = new AiServiceUnavailableException("http://localhost:8003", new Exception("timeout"));
            var sut = PipelineWhoseTtsThrows(ex);

            var result = await sut.RunAsync(Request(), CancellationToken.None);

            Assert.Equal(new WorkOutcome.Unavailable(ex.Message), result.Outcome);
        }

        /// <summary>An unmanaged (remote) endpoint's error rethrows unreported, and is ordinary failure.</summary>
        [Fact]
        public async Task AnyOtherException_IsFailed()
        {
            var sut = PipelineWhoseTtsThrows(new HttpRequestException("connection refused"));

            var result = await sut.RunAsync(Request(), CancellationToken.None);

            Assert.Equal(new WorkOutcome.Failed("connection refused"), result.Outcome);
        }

        /// <summary>
        /// An aborted run carries no audio, and its normalize/verify fields degrade to the same
        /// reason rather than to a null the recorder would have to guard.
        /// </summary>
        [Fact]
        public async Task AbortedRun_HasNoAudio_AndCarriesTheReasonThroughout()
        {
            var sut = PipelineWhoseTtsThrows(new InvalidOperationException("boom"));

            var result = await sut.RunAsync(Request(), CancellationToken.None);

            Assert.Empty(result.AudioBytes);
            Assert.False(result.Normalize.Ok);
            Assert.False(result.Verify.Ok);
            Assert.Equal("boom", result.Normalize.Reason);
            Assert.Equal("boom", result.Verify.Reason);
        }

        /// <summary>
        /// The one thing totality does not swallow: cancelling the caller's own token still unwinds,
        /// so a stopping queue does not record a wall of failures. Verbatim
        /// <c>ILlmCompletionRunner</c>'s contract.
        /// </summary>
        [Fact]
        public async Task CallerCancellation_StillThrows()
        {
            var sut = PipelineWhoseTtsThrows(new InvalidOperationException("never reached"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => sut.RunAsync(Request(), cts.Token));
        }

        // ── Phase 2 ───────────────────────────────────────────────────────────

        /// <summary>
        /// Audio's phase 2 has one case: a recorded item is finished, carrying the recorder's path so
        /// the queue's <c>Complete</c> arm can publish it. Elapsed stays null — one item is one unit
        /// of work here, so the store measures it from <c>MarkProcessing</c>.
        /// </summary>
        [Fact]
        public void DecideApplied_IsCompleteCarryingTheRecordedPath()
        {
            var d = AudioDisposition.DecideApplied("audio/ch1/item.wav");

            Assert.Equal(new Disposition.Complete(null, "audio/ch1/item.wav"), d);
        }
    }
}
