using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioPostProcessPreviewRendererTests : AppDbTestBase, IDisposable
    {
        private const string Root = "C:\\fake-workspace";
        private static readonly byte[] PreviewSource = [1, 2, 3];
        private static readonly byte[] Processed = [9, 9, 9];
        private static readonly ProjectFolderId Folder = new("book-a");

        private readonly FakeFileSystem _fs = new(Root);
        private readonly IPreviewSourceCache _previewSources;
        private readonly Guid _itemId = Guid.NewGuid();
        private readonly string _previewDir = Path.Combine(Path.GetTempPath(), $"r2m-preview-tests-{Guid.NewGuid():N}");
        private readonly AudioPreviewStore _store;
        private readonly string _token = Guid.NewGuid().ToString("N");

        private sealed class SpyStep(string stepId, PostProcessResult result) : IAudioPostProcessStep
        {
            public string StepId => stepId;
            public string? ReceivedSettingsJson { get; private set; }
            public string? ReceivedFfmpegPath { get; private set; }
            public byte[]? ReceivedWav { get; private set; }

            public Task<PostProcessResult> ProcessAsync(byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
            {
                ReceivedWav = wav;
                ReceivedFfmpegPath = ffmpegPath;
                ReceivedSettingsJson = settingsJson;
                return Task.FromResult(result);
            }
        }

        private sealed class StubProber : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ok"));
        }

        private AudioProcessingSettingsService NewSettings() =>
            new(Factory, new StubProber(), NullLogger<AudioProcessingSettingsService>.Instance);

        private AudioPostProcessPreviewRenderer NewRenderer(params IAudioPostProcessStep[] steps) =>
            new(steps, _previewSources, NewSettings(), _store,
                NullLogger<AudioPostProcessPreviewRenderer>.Instance);

        private static SpyStep SoftenStep(PostProcessResult? result = null) =>
            new(AudioPostProcessStepIds.ConsonantSoften,
                result ?? new PostProcessResult(Processed, Applied: true, Reason: null));

        private static SpyStep TrimStep(PostProcessResult? result = null) =>
            new(AudioPostProcessStepIds.SilenceTrim,
                result ?? new PostProcessResult(Processed, Applied: true, Reason: null));

        public AudioPostProcessPreviewRendererTests()
        {
            _store = new AudioPreviewStore(_previewDir);
            _previewSources = new PreviewSourceCache(_fs);
        }

        private async Task SeedPreviewSourceAsync() =>
            await _previewSources.SaveAsync(Folder, _itemId, PreviewSource);

        private static AudioPostProcessStepConfig SoftenDraft(bool enabled = false) =>
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften,
                enabled,
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.Deesser, Preset = ConsonantSoftenPresets.Light });

        private static AudioPostProcessStepConfig TrimDraft(bool enabled = false) =>
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.SilenceTrim, enabled, new SilenceTrimSettings(ThresholdDb: -42, PadMs: 20));

        [Fact]
        public async Task Runs_the_step_the_draft_names_not_the_other_registered_steps()
        {
            await SeedPreviewSourceAsync();
            var soften = SoftenStep();
            var trim = TrimStep();

            await NewRenderer(soften, trim).RenderAsync(_token, Folder, _itemId, TrimDraft());

            Assert.Equal(PreviewSource, trim.ReceivedWav);
            Assert.Null(soften.ReceivedWav);

            var settings = JsonSerializer.Deserialize<SilenceTrimSettings>(
                trim.ReceivedSettingsJson!, AudioPostProcessJson.Options)!;
            Assert.Equal(-42, settings.ThresholdDb);
            Assert.Equal(20, settings.PadMs);
        }

        [Fact]
        public async Task Unregistered_step_id_reports_a_reason_and_leaves_no_preview()
        {
            await SeedPreviewSourceAsync();

            var result = await NewRenderer(SoftenStep())
                .RenderAsync(_token, Folder, _itemId, TrimDraft());

            Assert.False(result.Applied);
            Assert.False(result.HasPreview);
            Assert.Contains(AudioPostProcessStepIds.SilenceTrim, result.Reason);
            Assert.False(_store.TryGetPath(_token, out _));
        }

        [Fact]
        public async Task Reports_the_byte_lengths_so_a_card_can_show_removed_ms()
        {
            await SeedPreviewSourceAsync();
            var trimmed = new byte[] { 1, 2 };
            var trim = TrimStep(new PostProcessResult(trimmed, Applied: true, Reason: null));

            var result = await NewRenderer(trim).RenderAsync(_token, Folder, _itemId, TrimDraft());

            Assert.Equal(PreviewSource.Length, result.OriginalBytes);
            Assert.Equal(trimmed.Length, result.OutputBytes);
        }

        [Fact]
        public async Task Processes_the_preview_source_not_the_stored_audio()
        {
            // The stored {id}.wav has already been through the steps; filtering it would stack the
            // step on itself. Only the Preview Source is unprocessed.
            await SeedPreviewSourceAsync();
            _fs.SeedFile(Path.Combine(Root, "book-a", "audio", $"{_itemId}.wav"), [7, 7, 7]);
            var step = SoftenStep();

            await NewRenderer(step).RenderAsync(_token, Folder, _itemId, SoftenDraft());

            Assert.Equal(PreviewSource, step.ReceivedWav);
        }

        [Fact]
        public async Task Runs_step_against_the_draft_settings_even_when_the_step_is_disabled()
        {
            // The preview auditions unsaved settings, so the saved enabled flag must not gate it.
            await SeedPreviewSourceAsync();
            var step = SoftenStep();
            await NewSettings().SetFfmpegPathAsync("C:\\tools\\ffmpeg.exe");

            var result = await NewRenderer(step).RenderAsync(_token, Folder, _itemId, SoftenDraft(enabled: false));

            Assert.True(result.Applied);
            Assert.Equal("C:\\tools\\ffmpeg.exe", step.ReceivedFfmpegPath);
            var settings = JsonSerializer.Deserialize<ConsonantSoftenSettings>(step.ReceivedSettingsJson!, AudioPostProcessJson.Options)!;
            Assert.Equal(ConsonantSoftenEngines.Deesser, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, settings.Preset);
        }

        [Fact]
        public async Task Stores_the_processed_wav_under_the_token()
        {
            await SeedPreviewSourceAsync();

            await NewRenderer(SoftenStep()).RenderAsync(_token, Folder, _itemId, SoftenDraft());

            Assert.True(_store.TryGetPath(_token, out var path));
            Assert.Equal(Processed, await File.ReadAllBytesAsync(path!));
        }

        [Fact]
        public async Task Skipped_step_reports_the_reason_and_stores_the_unprocessed_audio()
        {
            await SeedPreviewSourceAsync();
            var step = SoftenStep(new PostProcessResult(PreviewSource, Applied: false, Reason: "ffmpeg not found"));

            var result = await NewRenderer(step).RenderAsync(_token, Folder, _itemId, SoftenDraft());

            Assert.False(result.Applied);
            Assert.True(result.HasPreview);
            Assert.Equal("ffmpeg not found", result.Reason);
            Assert.True(_store.TryGetPath(_token, out var path));
            Assert.Equal(PreviewSource, await File.ReadAllBytesAsync(path!));
        }

        [Fact]
        public async Task Evicted_preview_source_reports_a_reason_and_leaves_no_preview()
        {
            var step = SoftenStep();

            var result = await NewRenderer(step).RenderAsync(_token, Folder, _itemId, SoftenDraft());

            Assert.False(result.Applied);
            Assert.False(result.HasPreview);
            Assert.NotNull(result.Reason);
            Assert.Null(step.ReceivedWav);
            Assert.False(_store.TryGetPath(_token, out _));
        }

        public void Dispose()
        {
            if (Directory.Exists(_previewDir))
                Directory.Delete(_previewDir, recursive: true);
        }
    }
}
