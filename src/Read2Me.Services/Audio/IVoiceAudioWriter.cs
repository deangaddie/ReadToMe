using Microsoft.Extensions.Logging;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Voice audio that is <b>arriving</b>: an upload from the Characters tab, a designed take from
    /// the voice-design server, the batch's sweep of both.
    /// </summary>
    public interface IVoiceAudioWriter
    {
        /// <summary>Stores an uploaded recording against a Voice. Returns its project-relative path.</summary>
        Task<string> RecordUploadedAsync(AudioStoreRequest request, CancellationToken ct = default);

        /// <summary>
        /// Stores a synthesised take against a designed Voice, together with the sample text it
        /// speaks and the prompt it was designed from. Returns its project-relative path.
        /// </summary>
        Task<string> RecordGeneratedAsync(
            AudioStoreRequest request, string transcript, string designPrompt, CancellationToken ct = default);
    }

    /// <summary>
    /// Voice audio that is <b>leaving</b>: the Voice itself is deleted, or it becomes a designed Voice
    /// with nothing left to clone from.
    /// <para>
    /// A separate seam from <see cref="IVoiceAudioWriter"/> because the two directions need different
    /// things. Producing audio needs the audio pipeline, which is an application-level service;
    /// removing it needs nothing but the file system — and the command handlers that delete a Voice
    /// have to be constructible from this assembly's own registrations.
    /// </para>
    /// </summary>
    public interface IVoiceAudioRemover
    {
        /// <summary>Removes a Voice, and — after that commits — the audio it named.</summary>
        Task<BookMutationOutcome> DeleteVoiceAsync(
            ProjectFolderId folder, Guid voiceId, CancellationToken ct = default);

        /// <summary>
        /// Switches a Voice between cloned-from-a-recording and designed-from-a-description, and —
        /// when that commits and the Voice has become designed — removes the recording there is no
        /// longer anything to clone from.
        /// </summary>
        Task<BookMutationOutcome> SetVoiceSourceAsync(
            ProjectFolderId folder, Guid voiceId, bool isGenerated, CancellationToken ct = default);
    }

    /// <summary>
    /// Keeps a Voice's audio on the safe side of the Book mutation that names it (ADR 0007).
    /// <para>
    /// The file is written <em>before</em> the mutation, so it is complete at the path the Book is
    /// about to name and another circuit can converge on the receipt and play it at once. A mutation
    /// that does not commit leaves nothing behind: the staged take is removed again, best-effort,
    /// unless it landed on the path the Voice already named — the Book still points there, and an
    /// empty path is the worse of the two states.
    /// </para>
    /// </summary>
    public sealed class VoiceAudioWriter(
        IAudioPipeline pipeline,
        ICharacterReader reader,
        IFileSystem fs,
        BookMutations mutations,
        ILogger<VoiceAudioWriter> logger) : IVoiceAudioWriter
    {
        public Task<string> RecordUploadedAsync(AudioStoreRequest request, CancellationToken ct = default) =>
            RecordAsync(request, path => new SetVoiceAudioMutation(request.FolderId, request.VoiceId, path), ct);

        public Task<string> RecordGeneratedAsync(
            AudioStoreRequest request, string transcript, string designPrompt, CancellationToken ct = default) =>
            RecordAsync(
                request,
                path => new SetVoiceGeneratedMutation(
                    request.FolderId, request.VoiceId, path, transcript, designPrompt),
                ct);

        /// <summary>
        /// Records the take, or throws. Both callers report a failed generation to the person who
        /// asked for it, which is the honest reading of an uncommitted write: nothing was stored, and
        /// the Voice still names whatever it named before.
        /// </summary>
        /// <exception cref="InvalidOperationException">The Book mutation did not commit.</exception>
        /// <exception cref="OperationCanceledException">It did not commit because it was cancelled.</exception>
        private async Task<string> RecordAsync(
            AudioStoreRequest request, Func<string, BookMutation> mutation, CancellationToken ct)
        {
            // Read before the write, because the pipeline is about to overwrite whatever sits at the
            // path it derives — after it, "what the Voice named" is unanswerable.
            var previous = (await reader.GetVoiceAsync(request.FolderId, request.VoiceId))?.AudioFileName;

            var relativePath = await pipeline.StoreAsync(request, ct);

            var outcome = await mutations.CommitAsync(mutation(relativePath), ct);
            if (outcome is BookMutationOutcome.Committed) return relativePath;

            DiscardStaged(request.FolderId, relativePath, previous);
            var failure = UncommittedArtifact.AsException(outcome, $"voice {request.VoiceId} audio", ct);
            logger.LogWarning("Voice {Id} audio was not recorded: {Reason}", request.VoiceId, failure.Message);
            throw failure;
        }

        private void DiscardStaged(ProjectFolderId folder, string relativePath, string? previous)
        {
            if (relativePath == previous) return;

            try
            {
                fs.DeleteProjectFile(folder, relativePath);
            }
            catch (Exception ex)
            {
                // Best-effort: a file nobody names is not worth failing the gesture over.
                logger.LogWarning(ex, "Could not remove the staged audio '{Path}'.", relativePath);
            }
        }
    }

    /// <summary>
    /// The other half of the same rule (ADR 0007): audio that is leaving goes <em>after</em> its
    /// mutation, never inside it.
    /// <para>
    /// Deleting the file first would leave a Voice naming audio that is already gone whenever the
    /// commit does not follow — a cancelled batch step is enough — and a Book that names an artifact
    /// it does not have is the defect the ordering rule exists to prevent, in either direction. So
    /// the row goes first, and only a committed outcome takes the file.
    /// </para>
    /// </summary>
    public sealed class VoiceAudioRemover(
        ICharacterReader reader,
        IFileSystem fs,
        IVoiceOriginalStore originals,
        BookMutations mutations,
        ILogger<VoiceAudioRemover> logger) : IVoiceAudioRemover
    {
        public async Task<BookMutationOutcome> DeleteVoiceAsync(
            ProjectFolderId folder, Guid voiceId, CancellationToken ct = default)
        {
            var named = await NamedAudioAsync(folder, voiceId);

            var outcome = await mutations.CommitAsync(new DeleteVoiceMutation(folder, voiceId), ct);
            if (outcome is BookMutationOutcome.Committed && named is { } audio)
                DropAudio(folder, audio.CharacterId, voiceId, audio.RelativePath);

            return outcome;
        }

        public async Task<BookMutationOutcome> SetVoiceSourceAsync(
            ProjectFolderId folder, Guid voiceId, bool isGenerated, CancellationToken ct = default)
        {
            var named = await NamedAudioAsync(folder, voiceId);

            var outcome = await mutations.CommitAsync(
                new SetVoiceSourceMutation(folder, voiceId, isGenerated), ct);

            // Only the designed direction drops a recording; the uploaded direction drops a design
            // prompt, which is a column and goes with the commit.
            if (outcome is BookMutationOutcome.Committed && isGenerated && named is { } audio)
                DropAudio(folder, audio.CharacterId, voiceId, audio.RelativePath);

            return outcome;
        }

        /// <summary>
        /// What the Voice names, read before the mutation and copied out of the row.
        /// <para>
        /// Copied, not held: the reader and the write side share this scope's tracking context, so the
        /// entity this returns is the one the mutation is about to null out or delete. Reading a
        /// property off it afterwards answers about the Book as it now is, which is exactly not the
        /// question.
        /// </para>
        /// </summary>
        private async Task<(Guid CharacterId, string? RelativePath)?> NamedAudioAsync(
            ProjectFolderId folder, Guid voiceId)
        {
            var voice = await reader.GetVoiceAsync(folder, voiceId);
            return voice is null ? null : (voice.CharacterId, voice.AudioFileName);
        }

        /// <summary>
        /// Removes the recording a committed mutation has stopped naming, and the stored original
        /// with it — <c>{voiceId}.orig.wav</c> existing <em>is</em> the claim that the live audio has
        /// been edited, so an original that outlived its audio would claim an edit on nothing.
        /// </summary>
        private void DropAudio(ProjectFolderId folder, Guid characterId, Guid voiceId, string? relativePath)
        {
            try
            {
                if (relativePath is not null) fs.DeleteProjectFile(folder, relativePath);
                originals.Delete(folder, characterId, voiceId);
            }
            catch (Exception ex)
            {
                // Best-effort: the Book is already right, and a leftover file is not worth turning a
                // committed gesture into a failed one.
                logger.LogWarning(ex, "Could not remove the audio '{Path}' of voice {Id}.",
                    relativePath, voiceId);
            }
        }
    }
}
