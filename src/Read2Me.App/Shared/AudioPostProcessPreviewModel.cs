using Read2Me.Services.Audio;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// Edit-state for a post-process card's A/B preview: which recent item is being auditioned,
    /// and the two player sources. Both sides are the item's <b>Preview Source</b> — unprocessed on
    /// the left, run through the draft settings on the right — so the comparison is off-vs-draft
    /// rather than saved-vs-saved-twice. Renders are on demand: the draft settings are only pushed
    /// through ffmpeg when the user asks, never per keystroke. One instance per card; the token keys
    /// this circuit's rendered file in <see cref="AudioPreviewStore"/>.
    /// </summary>
    public sealed class AudioPostProcessPreviewModel(IAudioPostProcessPreviewRenderer renderer)
    {
        private int _renderCount;

        public string Token { get; } = Guid.NewGuid().ToString("N");
        public RecentAudioSample? Sample { get; private set; }
        public bool Rendering { get; private set; }

        /// <summary>False after a render that fell back to the unprocessed audio; see <see cref="Reason"/>.</summary>
        public bool Applied { get; private set; }
        public string? Reason { get; private set; }

        /// <summary>Audio the step removed, for cards that trim. Null until a render lands.</summary>
        public double? RemovedMs { get; private set; }

        public string? OriginalUrl =>
            Sample is null ? null : $"/preview-source/{Uri.EscapeDataString(Sample.Folder.Value)}/{Sample.ParagraphItemId:D}";

        /// <summary>Cache-busted per render — the preview file is overwritten in place.</summary>
        public string? ProcessedUrl =>
            _renderCount == 0 ? null : $"/audio-preview/{Token}?v={_renderCount}";

        public void Select(RecentAudioSample sample)
        {
            Sample = sample;
            // The parked preview belongs to the previous sample — drop it rather than pair it
            // with the new original.
            _renderCount = 0;
            Applied = false;
            Reason = null;
            RemovedMs = null;
        }

        public async Task RenderAsync(AudioPostProcessStepConfig draft, CancellationToken ct = default)
        {
            if (Sample is null || Rendering) return;

            Rendering = true;
            try
            {
                var result = await renderer.RenderAsync(
                    Token, Sample.Folder, Sample.ParagraphItemId, draft, ct);

                Applied = result.Applied;
                Reason = result.Reason;
                RemovedMs = result is { HasPreview: true, Applied: true }
                    ? CanonicalWav.RemovedMs(result.OriginalBytes, result.OutputBytes)
                    : null;
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
