using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Audio;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Queueing;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.App.Audio
{
    /// <summary>
    /// Orchestration only: the events published, what reaches the pipeline and recorder, and the
    /// <see cref="Disposition"/> the processor decided. The queue is a <b>recorder</b>, so each test
    /// names that disposition rather than reading state back through the real queue — which cannot
    /// tell <c>RetryOnce</c> from <c>RetryAfter</c> at all.
    /// <para>
    /// Retry and settle <i>policy</i> is not here. It lives as a table in <c>QueueDispositionTests</c>
    /// (phase 1) and <c>AudioDispositionTests</c> (translation + phase 2), with no fakes.
    /// </para>
    /// </summary>
    public class AudioQueueProcessorTests
    {
        private readonly RecordingQueue _queue;
        private readonly FakeAudioItemResolver _resolver;
        private readonly FakeAudioItemPipeline _pipeline;
        private readonly FakeAudioResultRecorder _recorder;
        private readonly EventBroadcaster<AudioGenEvent> _broadcaster;
        private readonly List<AudioGenEvent> _events = new();
        private readonly ProjectFolderId _folder;
        private readonly AudioQueueProcessor _sut;

        private const string FolderName = "test-book";

        private static readonly PipelineRequest DefaultRequest = new(
            Folder: new ProjectFolderId(FolderName),
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
            Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "In a hole in the ground", Rescued: false),
            Outcome: new WorkOutcome.Ok());

        /// The pipeline is total, so an AI failure reaches the processor as a value, not a throw.
        private static PipelineResult AbortedResult(WorkOutcome outcome) => new(
            AudioBytes: [],
            Normalize: new NormalizeOutcome(Ok: false, Reason: outcome.Reason),
            Verify: new VerifyOutcome(Ok: false, Wer: null, Reason: outcome.Reason, Transcript: null, Rescued: false),
            Outcome: outcome);

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
            _queue = new RecordingQueue();
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

        /// <summary>The one disposition applied to <paramref name="item"/>.</summary>
        private T DispositionFor<T>(QueuedAudioItem item) where T : Disposition =>
            Assert.IsType<T>(Assert.Single(_queue.Applied, a => a.Item.Equals(item)).D);

        [Fact]
        public async Task Success_PublishesItemStarted_WithSpeakerAndText()
        {
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Single(_events.OfType<ItemStarted>(), e => e.Attempt == 1 && e.Character == "Bilbo");
        }

        /// <summary>
        /// The recorder's own product rides <see cref="Disposition.Complete.Product"/>, so the queue
        /// cannot complete an item without the path it needs to publish.
        /// </summary>
        [Fact]
        public async Task Success_CompletesWithRecorderReturnedPath()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _recorder.CannedRelativePath = "audio/canned.wav";
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Equal("audio/canned.wav", DispositionFor<Disposition.Complete>(queued).Product);
        }

        [Fact]
        public async Task ProcessItem_MarksProcessingFirst()
        {
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Equal(queued, Assert.Single(_queue.Processing));
        }

        [Fact]
        public async Task ResolutionFailure_PublishesItemStarted_ThenFailed()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = FailureResolution("No character assigned to item", speaker: null, text: null);
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Contains(_events, e => e is ItemStarted s && s.Character == null && s.Text == null);
            Assert.Contains(_events, e => e is Failed f && f.Reason.Contains("No character"));
            Assert.Contains("No character", DispositionFor<Disposition.Failed>(queued).Reason);
        }

        [Fact]
        public async Task ResolutionFailure_DoesNotCallPipeline()
        {
            _resolver.Result = FailureResolution("No voice");
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Null(_pipeline.LastRequest);
        }

        [Fact]
        public async Task PipelineFailed_FailsAndPublishesFailed()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _pipeline.Result = AbortedResult(new WorkOutcome.Failed("tts boom"));
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Contains(_events, e => e is Failed f && f.Reason.Contains("tts boom"));
            Assert.Equal("tts boom", DispositionFor<Disposition.Failed>(queued).Reason);
        }

        /// <summary>
        /// The only catch left in the processor's work, and it is not an AI seam — recording writes
        /// the audio file and can fail on ordinary I/O after a perfectly good pipeline run.
        /// </summary>
        [Fact]
        public async Task RecorderThrows_Fails()
        {
            var itemId = Guid.NewGuid();
            _resolver.Result = SuccessResolution(itemId);
            _recorder.Throws = new IOException("disk full");
            var queued = MakeItem(itemId);

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Contains(_events, e => e is Failed f && f.Reason.Contains("disk full"));
            Assert.Contains("disk full", DispositionFor<Disposition.Failed>(queued).Reason);
        }

        /// <summary>
        /// Resolution reads the book and voice settings, not an AI service. A throw there used to
        /// escape to the worker and leave the item stuck in Processing.
        /// </summary>
        [Fact]
        public async Task ResolverThrows_Fails()
        {
            _resolver.Throws = new InvalidOperationException("db gone");
            var queued = MakeItem();

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Equal("db gone", DispositionFor<Disposition.Failed>(queued).Reason);
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

            await _sut.ProcessItemAsync(queued, TestContext.Current.CancellationToken);

            Assert.Contains(_events, e => e is ItemStarted s && s.Character == "Bilbo" && s.Text == "Some text");
        }

        private sealed class RecordingQueue : IAudioQueue
        {
            public List<(QueuedAudioItem Item, Disposition D)> Applied { get; } = [];
            public List<QueuedAudioItem> Processing { get; } = [];

            public void Enqueue(IEnumerable<QueuedAudioItem> items) { }

            public void MarkProcessing(QueuedAudioItem item) => Processing.Add(item);

            public void Apply(QueuedAudioItem item, Disposition disposition) => Applied.Add((item, disposition));
        }
    }
}
