using Read2Me.AppData.Entities;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Fakes
{
    public class FakeAudioItemPipelineTests
    {
        private static PipelineRequest MakeRequest() => new(
            Folder: new Read2Me.Core.Models.ProjectFolderId("book-a"),
            ParagraphItemId: Guid.NewGuid(),
            SourceText: "Hello world",
            VoiceInstructions: null,
            RefAudioPath: "/ref.wav",
            TtsConfig: new ParagraphTtsServiceConfig(),
            TtsSettingsOverrideJson: null,
            MaxAttempts: 3,
            WerThreshold: 0.2,
            FfmpegPath: null,
            Speaker: null);

        [Fact]
        public async Task Returns_configured_result_and_captures_request()
        {
            var expected = new PipelineResult(
                AudioBytes: [1, 2, 3],
                Normalize: new NormalizeOutcome(Ok: true, Reason: null),
                Verify: new VerifyOutcome(Ok: true, Wer: 0.0, Reason: null, Transcript: "Hello world", Rescued: false));
            var fake = new FakeAudioItemPipeline { Result = expected };
            var req = MakeRequest();

            var result = await fake.RunAsync(req, CancellationToken.None);

            Assert.Same(expected, result);
            Assert.Same(req, fake.LastRequest);
        }

        [Fact]
        public async Task Throws_configured_exception()
        {
            var fake = new FakeAudioItemPipeline { Throws = new InvalidOperationException("tts failure") };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fake.RunAsync(MakeRequest(), CancellationToken.None));
        }
    }
}
