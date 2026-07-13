using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioResultRecorderTests
    {
        private readonly FakeFileSystem _fs;
        private readonly FakeBookCommandHandler _commands;
        private readonly AudioReviewService _reviews;
        private readonly AudioResultRecorder _sut;
        private readonly ProjectFolderId _folder;

        private const string FolderName = "test-book";
        private const string FakeRoot = @"C:\fake-workspace";

        private static readonly Guid ItemId = Guid.NewGuid();

        private static PipelineResult OkResult() => new(
            AudioBytes: [0x52, 0x49, 0x46, 0x46],
            Normalize: new NormalizeOutcome(Ok: true, Reason: null),
            Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "In a hole in the ground", Rescued: false));

        public AudioResultRecorderTests()
        {
            _folder = new ProjectFolderId(FolderName);
            _fs = new FakeFileSystem(FakeRoot);
            _fs.SeedFolder(FolderName);
            _commands = new FakeBookCommandHandler();
            _reviews = new AudioReviewService();
            _sut = new AudioResultRecorder(_fs, _commands, _reviews, NullLogger<AudioResultRecorder>.Instance);
        }

        [Fact]
        public async Task WritesWavFile_AtExpectedPath()
        {
            var id = Guid.NewGuid();
            await _sut.RecordAsync(_folder, id, OkResult(), "source text", CancellationToken.None);

            var expectedPath = Path.Combine(FakeRoot, FolderName, "audio", $"{id}.wav");
            Assert.True(_fs.FileExists(expectedPath));
        }

        [Fact]
        public async Task IssuesBothCommands()
        {
            await _sut.RecordAsync(_folder, ItemId, OkResult(), "source text", CancellationToken.None);

            Assert.Equal(2, _commands.Executed.Count);
            Assert.Contains(_commands.Executed, c => c is SetParagraphItemAudioCommand);
            Assert.Contains(_commands.Executed, c => c is SetAudioReviewCommand);
        }

        [Fact]
        public async Task NormalizeFail_SetsReview_WithNormalizeOkFalse()
        {
            var id = Guid.NewGuid();
            var result = new PipelineResult(
                AudioBytes: [0x52, 0x49, 0x46, 0x46],
                Normalize: new NormalizeOutcome(Ok: false, Reason: "ffmpeg failed"),
                Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "text", Rescued: false));

            await _sut.RecordAsync(_folder, id, result, "source text", CancellationToken.None);

            var review = _reviews.ReviewOf(_folder, id);
            Assert.NotNull(review);
            Assert.False(review!.NormalizeOk);
        }

        [Fact]
        public async Task VerifyFail_SetsReview_WithVerifyOkFalseAndWer()
        {
            var id = Guid.NewGuid();
            var result = new PipelineResult(
                AudioBytes: [0x52, 0x49, 0x46, 0x46],
                Normalize: new NormalizeOutcome(Ok: true, Reason: null),
                Verify: new VerifyOutcome(Ok: false, Wer: 0.42, Reason: "WER 0.42", Transcript: "wrong", Rescued: false));

            await _sut.RecordAsync(_folder, id, result, "source text", CancellationToken.None);

            var review = _reviews.ReviewOf(_folder, id);
            Assert.NotNull(review);
            Assert.False(review!.VerifyOk);
            Assert.Equal(0.42, review.Wer);
        }

        [Fact]
        public async Task BothOk_ClearsExistingReview()
        {
            var id = Guid.NewGuid();
            _reviews.Set(_folder, id, new AudioReviewInfo(
                AudioReviewState.NeedsReview, false, "stale", false, 0.9, "stale", null, null));

            await _sut.RecordAsync(_folder, id, OkResult(), "source text", CancellationToken.None);

            Assert.Null(_reviews.ReviewOf(_folder, id));
        }

        [Fact]
        public async Task Returns_ExpectedRelativePath()
        {
            var id = Guid.NewGuid();
            var path = await _sut.RecordAsync(_folder, id, OkResult(), "source text", CancellationToken.None);

            Assert.Equal($"audio/{id}.wav", path);
        }
    }
}
