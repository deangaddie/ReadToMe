using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Outcome of an A/B preview render. <see cref="HasPreview"/> false means nothing was stored
    /// for the token (the Preview Source could not be read); <see cref="Applied"/> false with a
    /// preview means the step fell back, so the stored WAV is the unprocessed audio.
    /// <see cref="Reason"/> says why in both cases. The byte lengths let a card report how much
    /// audio the step added or removed without a second ffmpeg pass — see <see cref="CanonicalWav"/>.
    /// </summary>
    public sealed record PreviewRenderResult(
        bool Applied, string? Reason, bool HasPreview, int OriginalBytes = 0, int OutputBytes = 0);

    public interface IAudioPostProcessPreviewRenderer
    {
        /// <summary>
        /// Renders <b>one</b> step — <paramref name="draft"/>'s — over an item's <b>Preview Source</b>
        /// using the card's unsaved settings, and parks the result in the preview store under
        /// <paramref name="token"/>. The draft's enabled flag is ignored: auditioning a step you have
        /// not turned on yet is the whole point of the preview.
        /// <para>
        /// One step, never the enabled chain: a card tunes <i>its</i> step, and the shipped steps do
        /// not interact audibly (silence-trim removes near-silent samples from the ends, soften EQs
        /// speech). The voice editor, whose steps <i>do</i> interact, renders a chain — both go
        /// through <see cref="IPreviewChainRenderer"/>.
        /// </para>
        /// </summary>
        Task<PreviewRenderResult> RenderAsync(
            string token, ProjectFolderId folder, Guid itemId,
            AudioPostProcessStepConfig draft, CancellationToken ct = default);
    }

    /// <summary>The paragraph-item adapter over <see cref="IPreviewChainRenderer"/>: a one-step chain.</summary>
    public class AudioPostProcessPreviewRenderer(
        IPreviewChainRenderer core,
        IPreviewSourceCache previewSources,
        AudioProcessingSettingsService settingsService,
        ILogger<AudioPostProcessPreviewRenderer> logger) : IAudioPostProcessPreviewRenderer
    {
        public async Task<PreviewRenderResult> RenderAsync(
            string token, ProjectFolderId folder, Guid itemId,
            AudioPostProcessStepConfig draft, CancellationToken ct = default)
        {
            byte[]? original;
            try
            {
                // The stored {id}.wav is already post-processed — filtering it would stack the step
                // on itself. Only the Preview Source is unprocessed.
                original = await previewSources.TryReadAsync(folder, itemId, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Preview source for item {ItemId} could not be read", itemId);
                return new PreviewRenderResult(false, "preview source could not be read", HasPreview: false);
            }

            if (original is null)
                return new PreviewRenderResult(false, "this sample's preview source has been evicted", HasPreview: false);

            var settings = await settingsService.GetAsync();
            var result = await core.RenderAsync(original, [draft], [token], settings.FfmpegPath, ct);

            // The chain drops an unregistered id, so an empty outcome list *is* "no such step" —
            // and nothing was parked under the token.
            if (result.Steps.Count == 0)
                return new PreviewRenderResult(false, $"{draft.StepId} step is not registered", HasPreview: false);

            var outcome = result.Steps[0];
            return new PreviewRenderResult(
                outcome.Applied, outcome.Reason, HasPreview: true,
                OriginalBytes: original.Length, OutputBytes: outcome.Audio.Length);
        }
    }
}
