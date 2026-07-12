using Read2Me.Services.Audio;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for the consonant-soften card's A/B preview: which recent item is being
    /// auditioned, and the two player sources. Both sides are the item's <b>Preview Source</b> —
    /// unfiltered on the left, run through the draft settings on the right — so the comparison is
    /// off-vs-draft rather than saved-vs-saved-twice. Renders are on demand: the draft settings are
    /// only pushed through ffmpeg when the user asks, never per keystroke. The token keys this
    /// circuit's rendered file in <see cref="AudioPreviewStore"/>.
    /// </summary>
    public sealed class ConsonantSoftenPreviewModel(IConsonantSoftenPreviewRenderer renderer)
    {
        private int _renderCount;

        public string Token { get; } = Guid.NewGuid().ToString("N");
        public RecentAudioSample? Sample { get; private set; }
        public bool Rendering { get; private set; }

        /// <summary>False after a render that fell back to the unfiltered audio; see <see cref="Reason"/>.</summary>
        public bool Applied { get; private set; }
        public string? Reason { get; private set; }

        public string? OriginalUrl =>
            Sample is null ? null : $"/preview-source/{Uri.EscapeDataString(Sample.Folder.Value)}/{Sample.ParagraphItemId:D}";

        /// <summary>Cache-busted per render — the preview file is overwritten in place.</summary>
        public string? FilteredUrl =>
            _renderCount == 0 ? null : $"/audio-preview/{Token}?v={_renderCount}";

        public void Select(RecentAudioSample sample)
        {
            Sample = sample;
            // The parked preview belongs to the previous sample — drop it rather than pair it
            // with the new original.
            _renderCount = 0;
            Applied = false;
            Reason = null;
        }

        public async Task RenderAsync(ConsonantSoftenForm form, CancellationToken ct = default)
        {
            if (Sample is null || Rendering) return;

            Rendering = true;
            try
            {
                var result = await renderer.RenderAsync(
                    Token, Sample.Folder, Sample.ParagraphItemId, form.BuildConfig(), ct);

                Applied = result.Applied;
                Reason = result.Reason;
                if (result.HasPreview)
                    _renderCount++;
            }
            finally
            {
                Rendering = false;
            }
        }
    }
}
