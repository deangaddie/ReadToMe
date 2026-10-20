using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The voice audio editor's two writes.
    /// <para>
    /// Neither goes through <see cref="Read2Me.Core.Audio.IAudioPipeline.StoreAsync"/>, and that is the
    /// whole reason this service exists: <c>StoreAsync</c> re-runs <c>loudnorm</c> — re-inflating the
    /// dead air the user just trimmed — and re-derives the file name from the voice's <i>name</i>, so a
    /// renamed voice would get a new path and the audio the row points at would be orphaned. Both writes
    /// therefore go straight to the voice's <b>existing</b> <c>AudioFileName</c>, and the file name does
    /// not change.
    /// </para>
    /// <para>
    /// The audio is still Project state, so each write commits as a Book mutation: the receipt is how
    /// open views hear of it. The new audio is in place before the commit, so a reader that converges
    /// on the receipt plays it; a commit that does not follow puts the old audio back (ADR 0007).
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
        /// <exception cref="InvalidOperationException">The Book mutation did not commit.</exception>
        Task ApplyAsync(VoiceAudioRef voice, byte[] processed, CancellationToken ct = default);

        /// <summary>
        /// Copies the original back over the live WAV, <b>then deletes it</b>. The delete is what keeps
        /// the invariant exact — a surviving copy would leave the <c>Edited</c> chip lying and
        /// <c>Restore original</c> lit forever. A no-op when the voice was never edited.
        /// </summary>
        /// <exception cref="InvalidOperationException">The Book mutation did not commit.</exception>
        Task RestoreOriginalAsync(VoiceAudioRef voice, CancellationToken ct = default);
    }

    public sealed class VoiceAudioEditor(
        IVoiceOriginalStore originals,
        IFileSystem fs,
        BookMutations mutations,
        ILogger<VoiceAudioEditor> logger) : IVoiceAudioEditor
    {
        public async Task ApplyAsync(VoiceAudioRef voice, byte[] processed, CancellationToken ct = default)
        {
            var previous = await ReadLiveAsync(voice, ct);
            var firstEdit = !originals.Exists(voice.Folder, voice.CharacterId, voice.VoiceId);

            await originals.CaptureIfAbsentAsync(
                voice.Folder, voice.CharacterId, voice.VoiceId, voice.LiveRelativePath, ct);
            await WriteLiveAsync(voice, processed);

            var outcome = await mutations.CommitAsync(
                new ApplyVoiceAudioEditMutation(voice.Folder, voice.VoiceId), ct);
            if (outcome is not BookMutationOutcome.Committed)
            {
                // An original captured for this edit goes with it: its existence is the claim that
                // the live audio has been edited.
                if (firstEdit) originals.Delete(voice.Folder, voice.CharacterId, voice.VoiceId);
                throw await PutBackAsync(voice, previous, outcome, ct);
            }

            logger.LogInformation(
                "Voice {VoiceId} audio edited — {Bytes} bytes ({Dur:0}ms) written over {Path}",
                voice.VoiceId, processed.Length, CanonicalWav.DurationMs(processed.Length), voice.LiveRelativePath);
        }

        public async Task RestoreOriginalAsync(VoiceAudioRef voice, CancellationToken ct = default)
        {
            var original = await originals.TryReadAsync(voice.Folder, voice.CharacterId, voice.VoiceId, ct);
            if (original is null) return;

            var previous = await ReadLiveAsync(voice, ct);
            await WriteLiveAsync(voice, original);

            var outcome = await mutations.CommitAsync(
                new RestoreVoiceAudioOriginalMutation(voice.Folder, voice.VoiceId), ct);
            if (outcome is not BookMutationOutcome.Committed)
                throw await PutBackAsync(voice, previous, outcome, ct);

            // Only after the commit: until then the edit is still the recorded state, and the original
            // is what claims it.
            originals.Delete(voice.Folder, voice.CharacterId, voice.VoiceId);

            logger.LogInformation("Voice {VoiceId} audio restored to its original", voice.VoiceId);
        }

        /// <summary>
        /// Puts the live audio back as it was before an uncommitted write, and answers the exception
        /// that reports it. Best-effort: failing to put it back is logged, not allowed to hide why the
        /// write failed.
        /// </summary>
        private async Task<Exception> PutBackAsync(
            VoiceAudioRef voice, byte[]? previous, BookMutationOutcome outcome, CancellationToken ct)
        {
            try
            {
                if (previous is null) fs.DeleteFile(LivePath(voice));
                else await WriteLiveAsync(voice, previous);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not put back the audio of voice {Id} at {Path}.",
                    voice.VoiceId, voice.LiveRelativePath);
            }

            var failure = UncommittedArtifact.AsException(outcome, $"voice {voice.VoiceId} audio", ct);
            logger.LogWarning("Voice {Id} audio edit was not recorded: {Reason}", voice.VoiceId, failure.Message);
            return failure;
        }

        private async Task<byte[]?> ReadLiveAsync(VoiceAudioRef voice, CancellationToken ct)
        {
            var path = LivePath(voice);
            if (!fs.FileExists(path)) return null;

            using var source = fs.OpenRead(path);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }

        private async Task WriteLiveAsync(VoiceAudioRef voice, byte[] bytes)
        {
            using var source = new MemoryStream(bytes);
            await fs.WriteFileAsync(LivePath(voice), source);
        }

        private string LivePath(VoiceAudioRef voice) =>
            Path.Combine(
                fs.GetProjectFolderPath(voice.Folder.Value),
                voice.LiveRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
