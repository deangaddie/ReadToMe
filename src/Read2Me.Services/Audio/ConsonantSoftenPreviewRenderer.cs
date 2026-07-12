using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Outcome of an A/B preview render. <see cref="HasPreview"/> false means nothing was stored
    /// for the token (the source audio could not be read); <see cref="Applied"/> false with a
    /// preview means the filter fell back, so the stored WAV is the unfiltered audio.
    /// <see cref="Reason"/> says why in both cases.
    /// </summary>
    public sealed record PreviewRenderResult(bool Applied, string? Reason, bool HasPreview);

    public interface IConsonantSoftenPreviewRenderer
    {
        /// <summary>
        /// Renders the consonant-soften step over a stored paragraph WAV using <paramref name="draft"/>
        /// — the card's unsaved settings — and parks the result in the preview store under
        /// <paramref name="token"/>. The draft's enabled flag is ignored: auditioning a step you have
        /// not turned on yet is the whole point of the preview.
        /// </summary>
        Task<PreviewRenderResult> RenderAsync(
            string token, ProjectFolderId folder, string audioRelativePath,
            AudioPostProcessStepConfig draft, CancellationToken ct = default);
    }

    public class ConsonantSoftenPreviewRenderer(
        IEnumerable<IAudioPostProcessStep> steps,
        IFileSystem fs,
        AudioProcessingSettingsService settingsService,
        AudioPreviewStore store,
        ILogger<ConsonantSoftenPreviewRenderer> logger) : IConsonantSoftenPreviewRenderer
    {
        public async Task<PreviewRenderResult> RenderAsync(
            string token, ProjectFolderId folder, string audioRelativePath,
            AudioPostProcessStepConfig draft, CancellationToken ct = default)
        {
            var step = steps.FirstOrDefault(s => s.StepId == AudioPostProcessStepIds.ConsonantSoften);
            if (step is null)
                return new PreviewRenderResult(false, "consonant-soften step is not registered", HasPreview: false);

            byte[] original;
            try
            {
                var path = Path.Combine(
                    fs.GetProjectFolderPath(folder.Value),
                    audioRelativePath.Replace('/', Path.DirectorySeparatorChar));

                using var source = fs.OpenRead(path);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, ct);
                original = buffer.ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Preview source audio '{Path}' could not be read", audioRelativePath);
                return new PreviewRenderResult(false, "source audio could not be read", HasPreview: false);
            }

            var settings = await settingsService.GetAsync();
            var result = await step.ProcessAsync(original, settings.FfmpegPath, draft.SettingsJson, ct);

            await store.SaveAsync(token, result.Audio, ct);

            return new PreviewRenderResult(result.Applied, result.Reason, HasPreview: true);
        }
    }
}
