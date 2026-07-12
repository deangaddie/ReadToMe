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
    public class ConsonantSoftenPreviewRendererTests : AppDbTestBase, IDisposable
    {
        private const string Root = "C:\\fake-workspace";
        private static readonly byte[] PreviewSource = [1, 2, 3];
        private static readonly byte[] Filtered = [9, 9, 9];
        private static readonly ProjectFolderId Folder = new("book-a");

        private readonly FakeFileSystem _fs = new(Root);
        private readonly IPreviewSourceCache _previewSources;
        private readonly Guid _itemId = Guid.NewGuid();
        private readonly string _previewDir = Path.Combine(Path.GetTempPath(), $"r2m-preview-tests-{Guid.NewGuid():N}");
        private readonly AudioPreviewStore _store;
        private readonly string _token = Guid.NewGuid().ToString("N");

        private sealed class SpyStep : IAudioPostProcessStep
        {
            private readonly PostProcessResult _result;
            public SpyStep(PostProcessResult result) => _result = result;

            public string StepId => AudioPostProcessStepIds.ConsonantSoften;
            public string? ReceivedSettingsJson { get; private set; }
            public string? ReceivedFfmpegPath { get; private set; }
            public byte[]? ReceivedWav { get; private set; }

            public Task<PostProcessResult> ProcessAsync(byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
            {
                ReceivedWav = wav;
                ReceivedFfmpegPath = ffmpegPath;
                ReceivedSettingsJson = settingsJson;
                return Task.FromResult(_result);
            }
        }

        private sealed class StubProber : IFfmpegProber
        {
            public Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default) =>
                Task.FromResult(new FfmpegProbeResult(true, "ok"));
        }

        private AudioProcessingSettingsService NewSettings() =>
            new(Factory, new StubProber(), NullLogger<AudioProcessingSettingsService>.Instance);

        private ConsonantSoftenPreviewRenderer NewRenderer(SpyStep step) =>
            new([step], _previewSources, NewSettings(), _store, NullLogger<ConsonantSoftenPreviewRenderer>.Instance);

        public ConsonantSoftenPreviewRendererTests()
        {
            _store = new AudioPreviewStore(_previewDir);
            _previewSources = new PreviewSourceCache(_fs);
        }

        private async Task SeedPreviewSourceAsync() =>
            await _previewSources.SaveAsync(Folder, _itemId, PreviewSource);

        private static AudioPostProcessStepConfig Draft(bool enabled = false) =>
            AudioPostProcessStepConfig.Create(
                AudioPostProcessStepIds.ConsonantSoften,
                enabled,
                new ConsonantSoftenSettings { Engine = ConsonantSoftenEngines.Deesser, Preset = ConsonantSoftenPresets.Light });

        [Fact]
        public async Task Filters_the_preview_source_not_the_stored_audio()
        {
            // The stored {id}.wav has already been through the steps; filtering it would stack the
            // step on itself. Only the Preview Source is unprocessed.
            await SeedPreviewSourceAsync();
            _fs.SeedFile(Path.Combine(Root, "book-a", "audio", $"{_itemId}.wav"), [7, 7, 7]);
            var step = new SpyStep(new PostProcessResult(Filtered, Applied: true, Reason: null));

            await NewRenderer(step).RenderAsync(_token, Folder, _itemId, Draft());

            Assert.Equal(PreviewSource, step.ReceivedWav);
        }

        [Fact]
        public async Task Runs_step_against_the_draft_settings_even_when_the_step_is_disabled()
        {
            // The preview auditions unsaved settings, so the saved enabled flag must not gate it.
            await SeedPreviewSourceAsync();
            var step = new SpyStep(new PostProcessResult(Filtered, Applied: true, Reason: null));
            await NewSettings().SetFfmpegPathAsync("C:\\tools\\ffmpeg.exe");

            var result = await NewRenderer(step).RenderAsync(_token, Folder, _itemId, Draft(enabled: false));

            Assert.True(result.Applied);
            Assert.Equal("C:\\tools\\ffmpeg.exe", step.ReceivedFfmpegPath);
            var settings = JsonSerializer.Deserialize<ConsonantSoftenSettings>(step.ReceivedSettingsJson!, AudioPostProcessJson.Options)!;
            Assert.Equal(ConsonantSoftenEngines.Deesser, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, settings.Preset);
        }

        [Fact]
        public async Task Stores_the_filtered_wav_under_the_token()
        {
            await SeedPreviewSourceAsync();
            var step = new SpyStep(new PostProcessResult(Filtered, Applied: true, Reason: null));

            await NewRenderer(step).RenderAsync(_token, Folder, _itemId, Draft());

            Assert.True(_store.TryGetPath(_token, out var path));
            Assert.Equal(Filtered, await File.ReadAllBytesAsync(path!));
        }

        [Fact]
        public async Task Skipped_step_reports_the_reason_and_stores_the_unfiltered_audio()
        {
            await SeedPreviewSourceAsync();
            var step = new SpyStep(new PostProcessResult(PreviewSource, Applied: false, Reason: "ffmpeg not found"));

            var result = await NewRenderer(step).RenderAsync(_token, Folder, _itemId, Draft());

            Assert.False(result.Applied);
            Assert.True(result.HasPreview);
            Assert.Equal("ffmpeg not found", result.Reason);
            Assert.True(_store.TryGetPath(_token, out var path));
            Assert.Equal(PreviewSource, await File.ReadAllBytesAsync(path!));
        }

        [Fact]
        public async Task Evicted_preview_source_reports_a_reason_and_leaves_no_preview()
        {
            var step = new SpyStep(new PostProcessResult(Filtered, Applied: true, Reason: null));

            var result = await NewRenderer(step).RenderAsync(_token, Folder, _itemId, Draft());

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
