using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ConsonantSoftenPreviewModelTests
    {
        private sealed class SpyRenderer(PreviewRenderResult result) : IConsonantSoftenPreviewRenderer
        {
            public int Calls { get; private set; }
            public string? ReceivedToken { get; private set; }
            public ProjectFolderId ReceivedFolder { get; private set; }
            public string? ReceivedAudioPath { get; private set; }
            public AudioPostProcessStepConfig? ReceivedDraft { get; private set; }

            public Task<PreviewRenderResult> RenderAsync(
                string token, ProjectFolderId folder, string audioRelativePath,
                AudioPostProcessStepConfig draft, CancellationToken ct = default)
            {
                Calls++;
                ReceivedToken = token;
                ReceivedFolder = folder;
                ReceivedAudioPath = audioRelativePath;
                ReceivedDraft = draft;
                return Task.FromResult(result);
            }
        }

        private static readonly PreviewRenderResult Ok = new(Applied: true, Reason: null, HasPreview: true);

        private static RecentAudioSample Sample(string folder = "book-a", string path = "audio/item.wav") =>
            new(folder, "Book A", Guid.NewGuid(), path, "hello there", "Alice", "Warm Alto");

        [Fact]
        public async Task Render_sends_the_cards_draft_settings_to_the_renderer()
        {
            var renderer = new SpyRenderer(Ok);
            var model = new ConsonantSoftenPreviewModel(renderer);
            var form = ConsonantSoftenForm.FromConfig(null);
            form.SetEngine(ConsonantSoftenEngines.Deesser);
            form.SetPreset(ConsonantSoftenPresets.Light);
            model.Select(Sample());

            await model.RenderAsync(form);

            Assert.Equal(1, renderer.Calls);
            Assert.Equal("book-a", renderer.ReceivedFolder.Value);
            Assert.Equal("audio/item.wav", renderer.ReceivedAudioPath);
            Assert.Equal(model.Token, renderer.ReceivedToken);

            var settings = JsonSerializer.Deserialize<ConsonantSoftenSettings>(
                renderer.ReceivedDraft!.SettingsJson!, AudioPostProcessJson.Options)!;
            Assert.Equal(ConsonantSoftenEngines.Deesser, settings.Engine);
            Assert.Equal(ConsonantSoftenPresets.Light, settings.Preset);
        }

        [Fact]
        public async Task Players_get_distinct_sources_after_a_render()
        {
            var model = new ConsonantSoftenPreviewModel(new SpyRenderer(Ok));
            model.Select(Sample());

            Assert.Equal("/workspace/book-a/audio/item.wav", model.OriginalUrl);
            Assert.Null(model.FilteredUrl);

            await model.RenderAsync(ConsonantSoftenForm.FromConfig(null));

            Assert.Equal($"/audio-preview/{model.Token}?v=1", model.FilteredUrl);
            Assert.NotEqual(model.OriginalUrl, model.FilteredUrl);
        }

        [Fact]
        public async Task Re_rendering_busts_the_players_cache()
        {
            var model = new ConsonantSoftenPreviewModel(new SpyRenderer(Ok));
            model.Select(Sample());

            await model.RenderAsync(ConsonantSoftenForm.FromConfig(null));
            var first = model.FilteredUrl;
            await model.RenderAsync(ConsonantSoftenForm.FromConfig(null));

            Assert.NotEqual(first, model.FilteredUrl);
        }

        [Fact]
        public async Task Selecting_another_sample_drops_the_stale_preview()
        {
            var model = new ConsonantSoftenPreviewModel(new SpyRenderer(Ok));
            model.Select(Sample());
            await model.RenderAsync(ConsonantSoftenForm.FromConfig(null));

            model.Select(Sample("book-b", "audio/other.wav"));

            Assert.Null(model.FilteredUrl);
            Assert.Equal("/workspace/book-b/audio/other.wav", model.OriginalUrl);
        }

        [Fact]
        public async Task Failed_render_surfaces_the_reason_and_serves_no_preview()
        {
            var renderer = new SpyRenderer(new PreviewRenderResult(false, "source audio could not be read", HasPreview: false));
            var model = new ConsonantSoftenPreviewModel(renderer);
            model.Select(Sample());

            await model.RenderAsync(ConsonantSoftenForm.FromConfig(null));

            Assert.False(model.Applied);
            Assert.Equal("source audio could not be read", model.Reason);
            Assert.Null(model.FilteredUrl);
        }

        [Fact]
        public async Task Render_without_a_sample_does_nothing()
        {
            var renderer = new SpyRenderer(Ok);
            var model = new ConsonantSoftenPreviewModel(renderer);

            await model.RenderAsync(ConsonantSoftenForm.FromConfig(null));

            Assert.Equal(0, renderer.Calls);
            Assert.Null(model.FilteredUrl);
        }
    }
}
