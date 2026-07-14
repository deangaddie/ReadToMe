using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The voice audio editor's two writes.
    /// <para>
    /// Neither goes through <see cref="Read2Me.Core.Audio.IAudioPipeline.StoreAsync"/>, and that is the
    /// whole reason this service exists: <c>StoreAsync</c> re-runs <c>loudnorm</c> — re-inflating the
    /// dead air the user just trimmed — and re-derives the file name from the voice's <i>name</i>, so a
    /// renamed voice would get a new path and the audio the row points at would be orphaned. Both writes
    /// therefore go straight to the voice's <b>existing</b> <c>AudioFileName</c>. Nothing is written to
    /// the database — the file name does not change.
    /// </para>
    /// </summary>
    public interface IVoiceAudioEditor
    {
        /// <summary>
        /// Captures the original if this is the first edit, then writes <paramref name="processed"/> over
        /// the voice's live WAV. The bytes are the last render's final step — Apply never re-renders, so
        /// "Apply stores exactly what the user heard" is true by construction (the page gates Apply on a
        /// fresh preview).
        /// </summary>
        Task ApplyAsync(VoiceAudioRef voice, byte[] processed, CancellationToken ct = default);

        /// <summary>
        /// Copies the original back over the live WAV, <b>then deletes it</b>. The delete is what keeps
        /// the invariant exact — a surviving copy would leave the <c>Edited</c> chip lying and
        /// <c>Restore original</c> lit forever. A no-op when the voice was never edited.
        /// </summary>
        Task RestoreOriginalAsync(VoiceAudioRef voice, CancellationToken ct = default);
    }

    public sealed class VoiceAudioEditor(
        IVoiceOriginalStore originals,
        IFileSystem fs,
        ILogger<VoiceAudioEditor> logger) : IVoiceAudioEditor
    {
        public async Task ApplyAsync(VoiceAudioRef voice, byte[] processed, CancellationToken ct = default)
        {
            await originals.CaptureIfAbsentAsync(
                voice.Folder, voice.CharacterId, voice.VoiceId, voice.LiveRelativePath, ct);

            using var source = new MemoryStream(processed);
            await fs.WriteFileAsync(LivePath(voice), source);

            logger.LogInformation(
                "Voice {VoiceId} audio edited — {Bytes} bytes ({Dur:0}ms) written over {Path}",
                voice.VoiceId, processed.Length, CanonicalWav.DurationMs(processed.Length), voice.LiveRelativePath);
        }

        public async Task RestoreOriginalAsync(VoiceAudioRef voice, CancellationToken ct = default)
        {
            var original = await originals.TryReadAsync(voice.Folder, voice.CharacterId, voice.VoiceId, ct);
            if (original is null) return;

            using var source = new MemoryStream(original);
            await fs.WriteFileAsync(LivePath(voice), source);

            originals.Delete(voice.Folder, voice.CharacterId, voice.VoiceId);

            logger.LogInformation("Voice {VoiceId} audio restored to its original", voice.VoiceId);
        }

        private string LivePath(VoiceAudioRef voice) =>
            Path.Combine(
                fs.GetProjectFolderPath(voice.Folder.Value),
                voice.LiveRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
