using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>Which voice is being edited, and where its live audio lives.</summary>
    /// <param name="LiveRelativePath">The voice's <c>AudioFileName</c> — project-relative, forward-slashed.</param>
    public sealed record VoiceAudioRef(
        ProjectFolderId Folder, Guid CharacterId, Guid VoiceId, string LiveRelativePath);

    /// <summary>
    /// A chain render for the voice audio editor: one preview token per step, so the page can stack a
    /// player per ticked step and the user hears the audio <i>as of</i> each one.
    /// </summary>
    /// <param name="Source">Null when the voice's audio could not be read — nothing was rendered.</param>
    public sealed record VoiceRenderResult(
        byte[]? Source, IReadOnlyList<ChainStepOutcome> Steps, string? Error = null)
    {
        /// <summary>The bytes an Apply would write: the last step's output.</summary>
        public byte[]? Final => Steps.Count > 0 ? Steps[^1].Audio : null;
    }

    public interface IVoicePreviewRenderer
    {
        /// <summary>
        /// Folds <paramref name="chain"/> over the voice's <b>original</b> — <c>{voiceId}.orig.wav</c>
        /// when the voice has been edited, else the live WAV, which is the original until the first
        /// Apply. Always the original, never the live audio of an edited voice: that is what stops a
        /// re-edit stacking filters on filters.
        /// </summary>
        Task<VoiceRenderResult> RenderChainAsync(
            VoiceAudioRef voice, IReadOnlyList<AudioPostProcessStepConfig> chain, IReadOnlyList<string> tokens,
            CancellationToken ct = default);
    }

    public sealed class VoicePreviewRenderer(
        IPreviewChainRenderer core,
        IVoiceOriginalStore originals,
        IFileSystem fs,
        AudioProcessingSettingsService settingsService,
        ILogger<VoicePreviewRenderer> logger) : IVoicePreviewRenderer
    {
        public async Task<VoiceRenderResult> RenderChainAsync(
            VoiceAudioRef voice, IReadOnlyList<AudioPostProcessStepConfig> chain, IReadOnlyList<string> tokens,
            CancellationToken ct = default)
        {
            byte[]? source;
            try
            {
                source = await ReadSourceAsync(voice, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Voice {VoiceId} audio could not be read for preview", voice.VoiceId);
                return new VoiceRenderResult(null, [], "the voice's audio could not be read");
            }

            if (source is null)
                return new VoiceRenderResult(null, [], "the voice has no audio to edit");

            var settings = await settingsService.GetAsync();
            var result = await core.RenderAsync(source, chain, tokens, settings.FfmpegPath, ct);

            return new VoiceRenderResult(source, result.Steps);
        }

        private async Task<byte[]?> ReadSourceAsync(VoiceAudioRef voice, CancellationToken ct)
        {
            var original = await originals.TryReadAsync(voice.Folder, voice.CharacterId, voice.VoiceId, ct);
            if (original is not null) return original;

            // No original stored means the voice has never been edited, so the live WAV *is* the
            // original — nothing post-normalize touches voice audio until this editor does.
            var livePath = Path.Combine(
                fs.GetProjectFolderPath(voice.Folder.Value),
                voice.LiveRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!fs.FileExists(livePath)) return null;

            using var live = fs.OpenRead(livePath);
            using var buffer = new MemoryStream();
            await live.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
    }
}
