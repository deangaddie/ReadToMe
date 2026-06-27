using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Audio;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Audio
{
    public class AudioQueueProcessorTests
    {
        private readonly AudioQueueService _queue;
        private readonly FakeAudioItemResolver _resolver;
        private readonly FakeAudioItemPipeline _pipeline;
        private readonly FakeAudioResultRecorder _recorder;
        private readonly EventBroadcaster<AudioGenEvent> _broadcaster;
        private readonly List<AudioGenEvent> _events = new();
        private readonly ProjectFolderId _folder;
        private readonly AudioQueueProcessor _sut;

        private const string FolderName = "test-book";

        private static readonly PipelineRequest DefaultRequest = new(
            ParagraphItemId: Guid.NewGuid(),
            SourceText: "In a hole in the ground",
            Speaker: "Bilbo",
            VoiceInstructions: "whispered",
            RefAudioPath: "/voices/bilbo/voice.wav",
            TtsConfig: new ParagraphTtsServiceConfig { Id = 1, Name = "Test", Type = ParagraphTtsServiceType.VoxCpm2, SettingsJson = "{}" },
            TtsSettingsOverrideJson: null,
            MaxAttempts: 1,
            WerThreshold: 0.15,
            FfmpegPath: "ffmpeg");

        private static PipelineResult OkResult() => new(
            AudioBytes: [0x52, 0x49, 0x46, 0x46],
            Normalize: new NormalizeOutcome(Ok: true, Reason: null),
            Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "In a hole in the ground", Rescued: false));

        private static ResolutionResult SuccessResolution(Guid? itemId = null) => new(
            Speaker: "Bilbo",
            SourceText: "In a hole in the ground",
            Request: DefaultRequest with { ParagraphItemId = itemId ?? DefaultRequest.ParagraphItemId },
            FailureReason: null);

        private static ResolutionResult FailureResolution(string reason, string? speaker = "Bilbo", string? text = "In a hole in the ground") =>
            new(Speaker: speaker, SourceText: text, Request: null, FailureReason: reason);

        public AudioQueueProcessorTests()
        {
            _folder = new ProjectFolderId(FolderName);
            _queue = new AudioQueueService();
            _broadcaster = new EventBroadcaster<AudioGenEvent>();
            _broadcaster.Event += e => _events.Add(e);

            _resolver = new FakeAudioItemResolver { Result = SuccessResolution() };
            _pipeline = new FakeAudioItemPipeline { Result = OkResult() };
            _recorder = new FakeAudioResultRecorder();

            _sut = new AudioQueueProcessor(
                _queue, _resolver, _pipeline, _recorder,
                _broadcaster, NullLogger<AudioQueueProcessor>.Instance);
        }

        private QueuedAudioItem MakeItem(Guid? itemId = null)
        {
            var id = itemId ?? Guid.NewGuid();
            var itemRef = new AudioItemRef(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            return new QueuedAudioItem(_folder, itemRef);
        }

        [Fact]
        public async Task Success_PublishesItemStarted_WithSpeakerAndText()
        {
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Single(_events.OfType<ItemStarted>(), e => e.Attempt == 1 && e.Character == "Bilbo");
        }

        [Fact]
        public async Task Success_MarkComplete_UsesRecorderReturnedPath()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _recorder.CannedRelativePath = "audio/canned.wav";
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Null(_queue.OutcomeOf(_folder, itemId));
        }

        [Fact]
        public async Task ResolutionFailure_PublishesItemStarted_ThenFailed()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = FailureResolution("No character assigned to item", speaker: null, text: null);
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Contains(_events, e => e is ItemStarted s && s.Character == null && s.Text == null);
            Assert.Contains(_events, e => e is Failed f && f.Reason.Contains("No character"));
            var outcome = _queue.OutcomeOf(_folder, itemId);
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Failed, outcome!.Kind);
        }

        [Fact]
        public async Task ResolutionFailure_DoesNotCallPipeline()
        {
            _resolver.Result = FailureResolution("No voice");
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Null(_pipeline.LastRequest);
        }

        [Fact]
        public async Task PipelineException_MarksFailedAndPublishesFailed()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _pipeline.Throws = new Exception("tts boom");
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Contains(_events, e => e is Failed f && f.Reason.Contains("tts boom"));
            var outcome = _queue.OutcomeOf(_folder, itemId);
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Failed, outcome!.Kind);
        }

        [Fact]
        public async Task Cancellation_PropagatesCleanly()
        {
            var queued = MakeItem();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _sut.ProcessItemAsync(queued, cts.Token));
        }

        [Fact]
        public async Task ResolverFailure_WithSpeakerAndText_PublishesItemStartedWithThem()
        {
            _resolver.Result = FailureResolution("No default voice for Bilbo", speaker: "Bilbo", text: "Some text");
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Contains(_events, e => e is ItemStarted s && s.Character == "Bilbo" && s.Text == "Some text");
        }
    }
}
