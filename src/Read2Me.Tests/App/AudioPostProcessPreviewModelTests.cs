using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App
{
    public class AudioPostProcessPreviewModelTests
    {
        private sealed class SpyRenderer(PreviewRenderResult result) : IAudioPostProcessPreviewRenderer
        {
            public int Calls { get; private set; }
            public string? ReceivedToken { get; private set; }
            public ProjectFolderId ReceivedFolder { get; private set; }
            public Guid ReceivedItemId { get; private set; }
            public AudioPostProcessStepConfig? ReceivedDraft { get; private set; }

            public Task<PreviewRenderResult> RenderAsync(
                string token, ProjectFolderId folder, Guid itemId,
                AudioPostProcessStepConfig draft, CancellationToken ct = default)
            {
                Calls++;
                ReceivedToken = token;
                ReceivedFolder = folder;
                ReceivedItemId = itemId;
                ReceivedDraft = draft;
                return Task.FromResult(result);
            }
        }

        private static readonly PreviewRenderResult Ok = new(Applied: true, Reason: null, HasPreview: true);

        private static AudioPostProcessStepConfig SoftenDraft() =>
            ConsonantSoftenForm.FromConfig(null).BuildConfig();

        private static RecentAudioSample Sample(string folder = "book-a") =>
            new(folder, "Book A", Guid.NewGuid(), "hello there", "Alice", "Warm Alto");

        [Fact]
        public async Task Render_sends_the_cards_draft_settings_to_the_renderer()
        {
            var renderer = new SpyRenderer(Ok);
            var model = new AudioPostProcessPreviewModel(renderer);
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetEngine(ConsonantSoftenEngines.Deesser);
            form.SetPreset(ConsonantSoftenPresets.Light);
            var sample = Sample();
            model.Select(sample);

            await model.RenderAsync(form.BuildConfig());

            Assert.Equal(1, renderer.Calls);
            Assert.Equal("book-a", renderer.ReceivedFolder.Value);
            Assert.Equal(sample.ParagraphItemId, renderer.ReceivedItemId);
            Assert.Equal(model.Token, renderer.ReceivedToken);

            var settings = JsonSerializer.Deserialize<ConsonantSoftenSettings>(
                renderer.ReceivedDraft!.SettingsJson!, AudioPostProcessJson.Options)!;
            Assert.Equal(ConsonantSoftenEngines.Deesser, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, settings.Preset);
        }

        [Fact]
        public async Task Render_carries_the_draft_step_id_so_each_card_previews_its_own_step()
        {
            var renderer = new SpyRenderer(Ok);
            var model = new AudioPostProcessPreviewModel(renderer);
            model.Select(Sample());

            await model.RenderAsync(SilenceTrimForm.FromConfig(null).BuildConfig());

            Assert.Equal(AudioPostProcessStepIds.SilenceTrim, renderer.ReceivedDraft!.StepId);
        }

        [Fact]
        public async Task Players_get_distinct_sources_after_a_render()
        {
            var model = new AudioPostProcessPreviewModel(new SpyRenderer(Ok));
            var sample = Sample();
            model.Select(sample);

            // The Original player must serve the Preview Source, never the post-processed {id}.wav.
            Assert.Equal($"/preview-source/book-a/{sample.ParagraphItemId:D}", model.OriginalUrl);
            Assert.Null(model.ProcessedUrl);

            await model.RenderAsync(SoftenDraft());

            Assert.Equal($"/audio-preview/{model.Token}?v=1", model.ProcessedUrl);
            Assert.NotEqual(model.OriginalUrl, model.ProcessedUrl);
        }

        [Fact]
        public async Task Re_rendering_busts_the_players_cache()
        {
            var model = new AudioPostProcessPreviewModel(new SpyRenderer(Ok));
            model.Select(Sample());

            await model.RenderAsync(SoftenDraft());
            var first = model.ProcessedUrl;
            await model.RenderAsync(SoftenDraft());

            Assert.NotEqual(first, model.ProcessedUrl);
        }

        [Fact]
        public async Task Selecting_another_sample_drops_the_stale_preview()
        {
            var model = new AudioPostProcessPreviewModel(new SpyRenderer(Ok));
            model.Select(Sample());
            await model.RenderAsync(SoftenDraft());

            var next = Sample("book-b");
            model.Select(next);

            Assert.Null(model.ProcessedUrl);
            Assert.Null(model.RemovedMs);
            Assert.Equal($"/preview-source/book-b/{next.ParagraphItemId:D}", model.OriginalUrl);
        }

        [Fact]
        public async Task Failed_render_surfaces_the_reason_and_serves_no_preview()
        {
            var renderer = new SpyRenderer(new PreviewRenderResult(false, "source audio could not be read", HasPreview: false));
            var model = new AudioPostProcessPreviewModel(renderer);
            model.Select(Sample());

            await model.RenderAsync(SoftenDraft());

            Assert.False(model.Applied);
            Assert.Equal("source audio could not be read", model.Reason);
            Assert.Null(model.ProcessedUrl);
        }

        [Fact]
        public async Task Render_without_a_sample_does_nothing()
        {
            var renderer = new SpyRenderer(Ok);
            var model = new AudioPostProcessPreviewModel(renderer);

            await model.RenderAsync(SoftenDraft());

            Assert.Equal(0, renderer.Calls);
            Assert.Null(model.ProcessedUrl);
        }

        [Fact]
        public async Task Removed_ms_comes_from_the_two_byte_lengths()
        {
            // One second in, half a second out: 500 ms of silence gone.
            var renderer = new SpyRenderer(new PreviewRenderResult(
                Applied: true, Reason: null, HasPreview: true,
                OriginalBytes: 44 + 48000, OutputBytes: 44 + 24000));
            var model = new AudioPostProcessPreviewModel(renderer);
            model.Select(Sample());

            await model.RenderAsync(SilenceTrimForm.FromConfig(null).BuildConfig());

            Assert.Equal(500, model.RemovedMs);
        }

        [Fact]
        public async Task Skipped_step_reports_no_removed_ms()
        {
            // The audio came back untouched, so "removed 0 ms" would read as a successful no-op trim.
            var renderer = new SpyRenderer(new PreviewRenderResult(
                Applied: false, Reason: "trim would remove nearly all audio", HasPreview: true,
                OriginalBytes: 44 + 48000, OutputBytes: 44 + 48000));
            var model = new AudioPostProcessPreviewModel(renderer);
            model.Select(Sample());

            await model.RenderAsync(SilenceTrimForm.FromConfig(null).BuildConfig());

            Assert.Null(model.RemovedMs);
            Assert.Equal("trim would remove nearly all audio", model.Reason);
        }
    }
}
