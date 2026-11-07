using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// The only writer of Voices into peer books (ADR 0018): a link's gains, a push or take (and
    /// catching up a book that differs from series), and copy after commit all write through here.
    /// <para>
    /// For each book, in the plan's order: any file already at a target path is set aside, the new
    /// files are written, and the Book mutation commits as that book's own History step. Then the
    /// set-aside files and every file no longer named go (ADR 0007: files exist before the Book
    /// names them, and go only after it stops). If the commit fails, the new files go and the
    /// set-aside ones are put back. A failure in one book doesn't stop the rest; that book is left
    /// <i>differs from series</i>, which is found live, not stored.
    /// </para>
    /// <para>
    /// It doesn't choose whom to write to: its caller passes the plan. It takes
    /// <see cref="SeriesRegistryLock"/> when called on its own, and runs inside the caller's hold
    /// when a registry write calls it.
    /// </para>
    /// </summary>
    public sealed class SeriesVoiceReplicator(
        IFileSystem fs,
        ICharacterReader characters,
        BookMutations mutations,
        SeriesRegistryLock gate,
        ILogger<SeriesVoiceReplicator> logger)
    {
        private const string BackupSuffix = ".series-backup";

        private static readonly AsyncLocal<bool> Writing = new();

        /// <summary>
        /// Set while this flow writes into peer books, so the series' commit hook stands aside for
        /// those commits: they aren't copied on, and don't wait for the series lock held here.
        /// </summary>
        internal static bool IsWriting => Writing.Value;

        /// <summary>Writes the plan under the series lock, for a caller that doesn't hold it (copy after commit).</summary>
        public async Task<IReadOnlyList<SeriesCopyResult>> WriteAsync(
            IReadOnlyList<SeriesVoiceWrite> plan, SeriesVoiceStep step, CancellationToken ct = default)
        {
            using var hold = await gate.EnterAsync(ct);
            return await WriteAsync(plan, step, hold, ct);
        }

        /// <summary>Writes the plan inside the caller's <paramref name="hold"/> of the series lock (a registry write).</summary>
        public async Task<IReadOnlyList<SeriesCopyResult>> WriteAsync(
            IReadOnlyList<SeriesVoiceWrite> plan, SeriesVoiceStep step, SeriesRegistryLock.Hold hold, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(hold);
            var results = new List<SeriesCopyResult>(plan.Count);
            var outer = Writing.Value;
            Writing.Value = true;
            try
            {
                foreach (var write in plan)
                {
                    BookMutationOutcome.Rejected? rejected;
                    try
                    {
                        rejected = await WriteOneAsync(write, step, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "'{Step}' threw while writing into '{Folder}'", step.Label, write.Book.Folder);
                        rejected = new BookMutationOutcome.Rejected(BookMutationRejection.Validation, ex.Message);
                    }
                    if (rejected is not null)
                        logger.LogWarning("'{Step}' was not written into '{Folder}': {Reason}", step.Label, write.Book.Folder, rejected.Message);
                    results.Add(new SeriesCopyResult(write.Book.Folder, rejected?.Message) { Rejection = rejected?.Reason });
                }
            }
            finally
            {
                Writing.Value = outer;
            }
            return results;
        }

        private async Task<BookMutationOutcome.Rejected?> WriteOneAsync(SeriesVoiceWrite write, SeriesVoiceStep step, CancellationToken ct)
        {
            var book = write.Book;
            if (write.Change is SeriesVoiceChange.Delete delete)
            {
                // Wherever the book holds the Voice; a book that doesn't hold it has nothing to delete.
                if (await characters.GetVoiceAsync(book.FolderId, delete.VoiceId) is not { } voice) return null;
                var held = new Held(voice.Id, voice.AudioFileName);
                var owner = book with { CharacterId = voice.CharacterId };
                var outcome = await mutations.CommitAsync(
                    new DeleteVoiceMutation(book.FolderId, delete.VoiceId) { CopiedStep = (step.Label, step.Action) }, ct);
                if (outcome is BookMutationOutcome.Rejected rejected) return rejected;
                RemoveUnnamed(owner, [held], []);
                return null;
            }

            // Read again for each book, never held: the reader shares this scope's tracking context.
            var before = (await characters.GetCharacterVoicesAsync(book.FolderId, book.CharacterId))
                .Select(v => new Held(v.Id, v.AudioFileName))
                .ToList();

            var staged = new StagedFiles(logger);
            try
            {
                var (sources, missingAudioFails) = write.Change switch
                {
                    SeriesVoiceChange.Gain gain => (gain.Voices, false),
                    SeriesVoiceChange.Copy copy => ([copy.Voice], true),
                    SeriesVoiceChange.Replace replace => (replace.Voices, true),
                    _ => throw new InvalidOperationException($"Unknown Voice change {write.Change}."),
                };
                var copies = new List<SeriesVoiceCopy>(sources.Count);
                foreach (var source in sources)
                {
                    var (audio, failure) = Stage(staged, source, book, missingAudioFails);
                    if (failure is not null)
                    {
                        staged.Rollback();
                        return new BookMutationOutcome.Rejected(BookMutationRejection.Validation, failure);
                    }
                    copies.Add(source.Voice with { AudioFileName = audio });
                }

                BookMutation mutation = write.Change switch
                {
                    SeriesVoiceChange.Gain => new GainSeriesVoicesMutation(book.FolderId, book.CharacterId, copies) { Step = (step.Label, step.Action) },
                    SeriesVoiceChange.Copy => new CopySeriesVoiceMutation(book.FolderId, book.CharacterId, copies[0], step.Label, step.Action),
                    _ => new ReplaceSeriesVoicesMutation(book.FolderId, book.CharacterId, copies, step.Label, step.Action),
                };
                var outcome = await mutations.CommitAsync(mutation, ct);
                if (outcome is BookMutationOutcome.Rejected rejected)
                {
                    staged.Rollback();
                    return rejected;
                }

                staged.Commit();
                var written = copies.Select(c => new Held(c.Id, c.AudioFileName)).ToList();
                var after = write.Change switch
                {
                    SeriesVoiceChange.Replace => written,
                    _ => before.Where(v => written.All(w => w.Id != v.Id)).Concat(written).ToList(),
                };
                RemoveUnnamed(book, before, after);
                return null;
            }
            catch
            {
                staged.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Writes one Voice's reference audio and kept original from its source book over the
        /// target's own, under the target Character's folder; with no audio, the target's kept
        /// original is set aside. Answers the audio path in the target, or why it failed. A source
        /// audio file that is missing fails the book when <paramref name="missingAudioFails"/>, and
        /// is otherwise copied as no audio.
        /// </summary>
        private (string? Audio, string? Failure) Stage(
            StagedFiles staged, SeriesVoiceSource source, SeriesPeer target, bool missingAudioFails)
        {
            var sourceFolder = fs.GetProjectFolderPath(source.Folder);
            var targetFolder = fs.GetProjectFolderPath(target.FolderId);
            var original = Path.Combine(targetFolder, VoiceOriginalStore.PathOf(target.CharacterId, source.Voice.Id));

            var sourceAudio = source.Voice.AudioFileName is { Length: > 0 } from ? Path.Combine(sourceFolder, from) : null;
            if (sourceAudio is not null && !File.Exists(sourceAudio))
            {
                if (missingAudioFails) return (null, $"the reference audio '{source.Voice.AudioFileName}' is missing.");
                sourceAudio = null;
            }
            if (sourceAudio is null)
            {
                staged.Remove(original);
                return (null, null);
            }

            var audio = $"voices/{target.CharacterId}/{Path.GetFileName(sourceAudio)}";
            staged.Write(sourceAudio, Path.Combine(targetFolder, audio));

            var sourceOriginal = Path.Combine(sourceFolder, VoiceOriginalStore.PathOf(source.CharacterId, source.Voice.Id));
            if (File.Exists(sourceOriginal)) staged.Write(sourceOriginal, original);
            else staged.Remove(original);
            return (audio, null);
        }

        /// <summary>After the commit: every file the book's Voices named before and no longer name goes.</summary>
        private void RemoveUnnamed(SeriesPeer book, IEnumerable<Held> before, IEnumerable<Held> after)
        {
            var folder = fs.GetProjectFolderPath(book.FolderId);
            var named = FilesOf(book, after).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in FilesOf(book, before).Where(f => !named.Contains(f)))
                TryDelete(Path.Combine(folder, file));
        }

        private static IEnumerable<string> FilesOf(SeriesPeer book, IEnumerable<Held> voices)
        {
            foreach (var voice in voices)
            {
                if (voice.Audio is { Length: > 0 } audio) yield return audio.Replace('\\', '/');
                yield return VoiceOriginalStore.PathOf(book.CharacterId, voice.Id).Replace('\\', '/');
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the Voice file '{File}'.", path);
            }
        }

        /// <summary>A Voice a book holds, by the files it names.</summary>
        private sealed record Held(Guid Id, string? Audio);

        /// <summary>
        /// Files written over a book's own before the commit that names them: each one already there
        /// is set aside, and put back on <see cref="Rollback"/> or dropped on <see cref="Commit"/>.
        /// </summary>
        private sealed class StagedFiles(ILogger logger)
        {
            private readonly List<string> _written = [];
            private readonly List<string> _setAside = [];

            public void Write(string from, string to)
            {
                SetAside(to);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // The book's own file is set aside by now; a file still there is one this write made.
                File.Copy(from, to, overwrite: true);
                _written.Add(to);
            }

            public void Remove(string path) => SetAside(path);

            private void SetAside(string path)
            {
                if (!File.Exists(path) || _setAside.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
                File.Move(path, path + BackupSuffix, overwrite: true);
                _setAside.Add(path);
            }

            public void Commit()
            {
                foreach (var path in _setAside)
                    Try(() => File.Delete(path + BackupSuffix), path);
                _written.Clear();
                _setAside.Clear();
            }

            public void Rollback()
            {
                foreach (var path in _written)
                    Try(() => File.Delete(path), path);
                foreach (var path in _setAside)
                    Try(() => File.Move(path + BackupSuffix, path, overwrite: true), path);
                _written.Clear();
                _setAside.Clear();
            }

            private void Try(Action action, string path)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not tidy the copied Voice file '{File}'.", path);
                }
            }
        }
    }

    /// <summary>A Voice as its source book holds it: where its files are, and the row to copy.</summary>
    public sealed record SeriesVoiceSource(ProjectFolderId Folder, Guid CharacterId, SeriesVoiceCopy Voice)
    {
        /// <summary>A Voice as <paramref name="folder"/> holds it, copied off the row (never held: the reader shares its scope's tracking context).</summary>
        public static SeriesVoiceSource Of(ProjectFolderId folder, Data.Entities.Voice v) => new(folder, v.CharacterId, new SeriesVoiceCopy(
            v.Id, v.Name, v.Description, v.Source, v.DesignPrompt, v.Transcript, v.AudioFileName,
            v.VoiceDesignSettingsOverrideJson, v.TtsSettingsOverrideJson, v.CreatedUtc));
    }

    /// <summary>One book's write in a replicator plan: the book's linked Character, and what it takes.</summary>
    public sealed record SeriesVoiceWrite(SeriesPeer Book, SeriesVoiceChange Change);

    /// <summary>What a book's linked Character takes from the series.</summary>
    public abstract record SeriesVoiceChange
    {
        private SeriesVoiceChange() { }

        /// <summary>A link's gains: these Voices, which the Character lacks, are added.</summary>
        public sealed record Gain(IReadOnlyList<SeriesVoiceSource> Voices) : SeriesVoiceChange;

        /// <summary>Copy after commit: one Voice's whole state, added or written over the book's own.</summary>
        public sealed record Copy(SeriesVoiceSource Voice) : SeriesVoiceChange;

        /// <summary>A push or take, or catching up: the Character holds exactly these Voices afterwards.</summary>
        public sealed record Replace(IReadOnlyList<SeriesVoiceSource> Voices) : SeriesVoiceChange;

        /// <summary>Copy after commit of a delete: the Voice goes, if the book holds it.</summary>
        public sealed record Delete(Guid VoiceId) : SeriesVoiceChange;
    }

    /// <summary>The History step each book's write lands in.</summary>
    public sealed record SeriesVoiceStep(string Label, StepAction Action);
}
