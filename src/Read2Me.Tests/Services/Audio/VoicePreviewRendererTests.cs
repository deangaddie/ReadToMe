using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoicePreviewRendererTests : AppDbTestBase, IDisposable
    {
        private const string Root = "C:\\fake-workspace";
        private static readonly ProjectFolderId Folder = new("book-a");

        private readonly FakeFileSystem _fs = new(Root);
        private readonly AudioPreviewStore _store;
        private readonly string _previewDir =
            Path.Combine(Path.GetTempPath(), $"r2m-voice-preview-tests-{Guid.NewGuid():N}");
        private readonly VoiceOriginalStore _originals;
        private readonly Guid _charId = Guid.NewGuid();
        private readonly Guid _voiceId = Guid.NewGuid();

        public VoicePreviewRendererTests()
        {
            _store = new AudioPreviewStore(_previewDir);
            _originals = new VoiceOriginalStore(_fs);
        }

        private sealed class MarkerStep(string stepId, byte marker, bool applied = true, string? reason = null)
            : IAudioPostProcessStep
        {
            public string StepId => stepId;
            public byte[]? ReceivedWav { get; private set; }

            public Task<PostProcessResult> ProcessAsync(
                byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
            {
                ReceivedWav ??= wav;
                var audio = applied ? wav.Append(marker).ToArray() : wav;
                return Task.FromResult(new PostProcessResult(audio, applied, reason));
            }
        }

        private sealed class StubProber : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ok"));
        }

        private VoicePreviewRenderer NewRenderer(params IAudioPostProcessStep[] steps) =>
            new(new PreviewChainRenderer(
                    new AudioPostProcessChain(steps, NullLogger<AudioPostProcessChain>.Instance), _store),
                _originals, _fs,
                new AudioProcessingSettingsService(Factory, new StubProber(), NullLogger<AudioProcessingSettingsService>.Instance),
                NullLogger<VoicePreviewRenderer>.Instance);

        private VoiceAudioRef Voice => new(Folder, _charId, _voiceId, LiveRelative);

        private string LiveRelative => $"voices/{_charId}/{_voiceId}-my-voice.wav";

        private string LivePath => Path.Combine(
            Root, "book-a", "voices", _charId.ToString(), $"{_voiceId}-my-voice.wav");

        private string OriginalPath => Path.Combine(
            Root, "book-a", "voices", _charId.ToString(), $"{_voiceId}.orig.wav");

        private static AudioPostProcessStepConfig Config(string stepId) =>
            AudioPostProcessStepConfig.Create(stepId, enabled: true, new DenoiseSettings());

        private string Token(string stepId) => $"page1-{stepId}";

        [Fact]
        public async Task A_three_step_chain_parks_a_token_per_step()
        {
            _fs.SeedFile(LivePath, [0]);
            var renderer = NewRenderer(new MarkerStep("a", 1), new MarkerStep("b", 2), new MarkerStep("c", 3));

            var result = await renderer.RenderChainAsync(
                Voice, [Config("a"), Config("b"), Config("c")], [Token("a"), Token("b"), Token("c")]);

            Assert.Equal(3, result.Steps.Count);
            Assert.True(_store.TryGetPath(Token("a"), out var a));
            Assert.True(_store.TryGetPath(Token("b"), out var b));
            Assert.True(_store.TryGetPath(Token("c"), out var c));
            // Each player holds the audio *as of* its step — cumulative, not isolated.
            Assert.Equal([0, 1], await File.ReadAllBytesAsync(a!));
            Assert.Equal([0, 1, 2], await File.ReadAllBytesAsync(b!));
            Assert.Equal([0, 1, 2, 3], await File.ReadAllBytesAsync(c!));
            Assert.Equal([0, 1, 2, 3], result.Final);
        }

        [Fact]
        public async Task Re_rendering_reuses_the_same_tokens_so_the_store_does_not_grow()
        {
            // AudioPreviewStore's token->path dictionary never evicts, and tuning dials means pressing
            // Preview repeatedly. Tokens are minted per page, not per render.
            _fs.SeedFile(LivePath, [0]);
            var renderer = NewRenderer(new MarkerStep("a", 1));

            await renderer.RenderChainAsync(Voice, [Config("a")], [Token("a")]);
            await renderer.RenderChainAsync(Voice, [Config("a")], [Token("a")]);

            Assert.True(_store.TryGetPath(Token("a"), out var path));
            Assert.Single(Directory.GetFiles(_previewDir));
            Assert.Equal([0, 1], await File.ReadAllBytesAsync(path!));
        }

        [Fact]
        public async Task A_skipped_step_still_parks_a_token_and_reports_why()
        {
            _fs.SeedFile(LivePath, [0]);
            var renderer = NewRenderer(new MarkerStep("a", 1, applied: false, reason: "ffmpeg not found"));

            var result = await renderer.RenderChainAsync(Voice, [Config("a")], [Token("a")]);

            var outcome = Assert.Single(result.Steps);
            Assert.False(outcome.Applied);
            Assert.Equal("ffmpeg not found", outcome.Reason);
            Assert.True(_store.TryGetPath(Token("a"), out var path));
            Assert.Equal([0], await File.ReadAllBytesAsync(path!));
        }

        [Fact]
        public async Task The_source_is_the_stored_original_when_the_voice_has_been_edited()
        {
            // Always the original, never the edited live WAV — that is what stops a re-edit stacking
            // filters on filters.
            _fs.SeedFile(LivePath, [9]);
            _fs.SeedFile(OriginalPath, [0]);
            var step = new MarkerStep("a", 1);

            var result = await NewRenderer(step).RenderChainAsync(Voice, [Config("a")], [Token("a")]);

            Assert.Equal([0], step.ReceivedWav);
            Assert.Equal([0], result.Source);
        }

        [Fact]
        public async Task The_source_falls_back_to_the_live_wav_when_there_is_no_original()
        {
            // No original means the voice has never been edited, so the live WAV *is* the original.
            _fs.SeedFile(LivePath, [5]);
            var step = new MarkerStep("a", 1);

            await NewRenderer(step).RenderChainAsync(Voice, [Config("a")], [Token("a")]);

            Assert.Equal([5], step.ReceivedWav);
        }

        [Fact]
        public async Task A_voice_with_no_audio_reports_an_error_and_renders_nothing()
        {
            var result = await NewRenderer(new MarkerStep("a", 1))
                .RenderChainAsync(Voice, [Config("a")], [Token("a")]);

            Assert.Null(result.Source);
            Assert.Empty(result.Steps);
            Assert.NotNull(result.Error);
            Assert.False(_store.TryGetPath(Token("a"), out _));
        }

        public void Dispose()
        {
            if (Directory.Exists(_previewDir))
                Directory.Delete(_previewDir, recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
