using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Audio;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
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
        private readonly FakeBookCommandHandler _commands;
        private readonly FakeFileSystem _fs;
        private readonly AudioReviewService _reviews;
        private readonly AudioGenBroadcaster _broadcaster;
        private readonly List<AudioGenEvent> _events = new();
        private readonly ProjectFolderId _folder;
        private readonly AudioQueueProcessor _sut;

        private const string FolderName = "test-book";
        private const string FakeRoot = @"C:\fake-workspace";

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
            _fs = new FakeFileSystem(FakeRoot);
            _fs.SeedFolder(FolderName);
            _reviews = new AudioReviewService();
            _broadcaster = new AudioGenBroadcaster();
            _broadcaster.Event += e => _events.Add(e);
            _commands = new FakeBookCommandHandler();

            _resolver = new FakeAudioItemResolver { Result = SuccessResolution() };
            _pipeline = new FakeAudioItemPipeline { Result = OkResult() };

            _sut = new AudioQueueProcessor(
                _queue, _resolver, _pipeline, _commands, _fs,
                _reviews, _broadcaster, NullLogger<AudioQueueProcessor>.Instance);
        }

        private QueuedAudioItem MakeItem(Guid? itemId = null)
        {
            var id = itemId ?? Guid.NewGuid();
            var itemRef = new AudioItemRef(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            return new QueuedAudioItem(_folder, itemRef);
        }

        [Fact]
        public async Task Success_WritesWavFile_AndCompletesQueue()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            var expectedPath = Path.Combine(FakeRoot, FolderName, "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expectedPath));
            Assert.Null(_queue.OutcomeOf(_folder, itemId));
        }

        [Fact]
        public async Task Success_PublishesItemStarted_WithSpeakerAndText()
        {
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Single(_events.OfType<ItemStarted>(), e => e.Attempt == 1 && e.Character == "Bilbo");
        }

        [Fact]
        public async Task Success_ExecutesBothCommands()
        {
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Equal(2, _commands.Executed.Count);
            Assert.Contains(_commands.Executed, c => c is SetParagraphItemAudioCommand);
            Assert.Contains(_commands.Executed, c => c is SetAudioReviewCommand);
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
        public async Task NormalizeOk_False_SetsReviewService()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _pipeline.Result = new PipelineResult(
                AudioBytes: [0x52, 0x49, 0x46, 0x46],
                Normalize: new NormalizeOutcome(Ok: false, Reason: "ffmpeg failed"),
                Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "text", Rescued: false));
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            var review = _reviews.ReviewOf(_folder, itemId);
            Assert.NotNull(review);
            Assert.False(review!.NormalizeOk);
        }

        [Fact]
        public async Task VerifyOk_False_SetsReviewService()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _pipeline.Result = new PipelineResult(
                AudioBytes: [0x52, 0x49, 0x46, 0x46],
                Normalize: new NormalizeOutcome(Ok: true, Reason: null),
                Verify: new VerifyOutcome(Ok: false, Wer: 0.42, Reason: "WER 0.42", Transcript: "wrong", Rescued: false));
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            var review = _reviews.ReviewOf(_folder, itemId);
            Assert.NotNull(review);
            Assert.False(review!.VerifyOk);
            Assert.Equal(0.42, review.Wer);
        }

        [Fact]
        public async Task BothOutcomesOk_ClearsExistingReview()
        {
            var itemId = Guid.NewGuid();
            _reviews.Set(_folder, itemId, new AudioReviewInfo(
                AudioReviewState.NeedsReview, false, "stale", false, 0.9, "stale", null, null));
            _resolver.Result = SuccessResolution(itemId);
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, CancellationToken.None);

            Assert.Null(_reviews.ReviewOf(_folder, itemId));
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
