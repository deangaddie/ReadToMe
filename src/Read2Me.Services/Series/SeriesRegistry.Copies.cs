using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Shared Voice edits (ADR 0018, copy after commit; #213 "Shared Voices"): a Voice of a
    /// series-linked Character is edited through the per-book routes, and once that commits its
    /// whole state is copied into every other linked book, in Series position order, each copy a
    /// History step there with the edit's label. A failed copy is logged and the rest carry on; that
    /// book then differs from series (#222), which is found live, not stored.
    /// </summary>
    public sealed partial class SeriesRegistry
    {
        private const string BackupSuffix = ".series-backup";

        // ---- reads -------------------------------------------------------------------------------

        /// <summary>
        /// The Character's Series character and the other books linked to it, in Series position
        /// order; null when the Character is linked to none (or its book is in no series).
        /// </summary>
        public async Task<SeriesPeers?> PeersAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            return await PeersCoreAsync(folder, characterId, ct);
        }

        /// <summary>
        /// Whether deleting <paramref name="voiceId"/> of a linked Character is refused (#205 item 9):
        /// another book's positional rules use it, or it is the last Voice of another book that holds it.
        /// </summary>
        public async Task<SeriesVoiceDeleteBlock?> VoiceDeleteBlockAsync(SeriesPeers peers, Guid voiceId) =>
            SeriesVoiceDeleteRules.Check(await DeleteUsesAsync(peers.Others, voiceId, VoiceIdsAsync));

        /// <summary>
        /// The inline series Voice editor's read (#213): the Voices of the series copy, each linked
        /// book with its Character and its default, and the Voices whose delete is refused.
        /// </summary>
        public async Task<SeriesResult<SeriesCharacterView>> CharacterAsync(
            Guid seriesId, Guid seriesCharacterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            if (state.Links.All(l => l.SeriesCharacterId != seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;

            var held = await HeldAsync(seriesId, LinkedPeers(state, seriesCharacterId));
            if (Choose(held) is not { } choice) return SeriesRefusal.UnknownSeriesCharacter;

            var books = new List<SeriesCharacterBook>();
            foreach (var h in held)
            {
                var name = state.Find(state.Books.First(b => b.Folder == h.Book.Folder).ProjectId, h.Book.CharacterId)!.Name;
                var defaultVoice = await characters.GetDefaultVoiceIdAsync(h.Book.FolderId, h.Book.CharacterId);
                books.Add(new SeriesCharacterBook(
                    h.Book.Folder, h.Book.Title, h.Book.Position, h.Book.CharacterId, name, defaultVoice, choice.DiffersFrom(h.Book.Folder)));
            }

            // The editor shows and edits the series copy; a delete there is judged as if made in that book.
            var copy = held.First(h => h.Book.Folder == choice.SeriesCopyFolder);
            var others = held.Where(h => h != copy).Select(h => h.Book).ToList();
            var idsByBook = held.ToDictionary(h => h.Book.Folder, h => (IReadOnlyCollection<Guid>)h.Voices.Select(v => v.Id).ToList());
            var blocks = new Dictionary<Guid, SeriesVoiceDeleteBlock>();
            foreach (var voice in copy.Voices)
            {
                var uses = await DeleteUsesAsync(others, voice.Id, b => Task.FromResult(idsByBook[b.Folder]));
                if (SeriesVoiceDeleteRules.Check(uses) is { } block)
                    blocks[voice.Id] = block;
            }
            return new SeriesCharacterView(copy.Book.Folder, copy.Book.CharacterId, copy.Voices, books, blocks);
        }

        // ---- copies ------------------------------------------------------------------------------

        /// <summary>
        /// Copies the Voice's whole state from <paramref name="source"/> into every other linked book:
        /// its row, reference audio and kept original, each book's copy a History step labelled
        /// <paramref name="label"/>. A book in <paramref name="differing"/> (it differed from the
        /// series copy the edit was made in) takes the whole set instead, so the edit brings it back
        /// in line. Then signals the series.
        /// </summary>
        public async Task<IReadOnlyList<SeriesCopyResult>> CopyVoiceAsync(
            SeriesPeers peers, Guid voiceId, string label, StepAction action, IReadOnlySet<string> differing)
        {
            using var _ = await gate.EnterAsync(CancellationToken.None);
            var source = peers.Self.FolderId;
            var results = await SeriesCopies.RunAsync(peers.Others, async book =>
            {
                if (differing.Contains(book.Folder))
                    return await ReplaceVoicesInAsync(book, peers.Self, label, action);

                // Read again for each book, never held: the reader shares this scope's tracking context.
                if (await characters.GetVoiceAsync(source, voiceId) is not { } voice)
                    return "the Voice is gone from the book it was edited in.";
                return await CopyIntoAsync(book, source, voice.CharacterId, CopyOf(voice), label, action);
            });
            Report(peers, voiceId, results);
            Signal(peers.SeriesId, SeriesChangeKind.Changed);
            return results;
        }

        /// <summary>
        /// Deletes the Voice from every other linked book, as it was deleted in the first. Each
        /// book's default rule moves to its oldest remaining Voice. Then signals the series.
        /// </summary>
        public async Task<IReadOnlyList<SeriesCopyResult>> CopyDeleteAsync(SeriesPeers peers, Guid voiceId, string label, StepAction action)
        {
            using var _ = await gate.EnterAsync(CancellationToken.None);
            var results = await SeriesCopies.RunAsync(peers.Others, async book =>
            {
                if (await characters.GetVoiceAsync(book.FolderId, voiceId) is not { } voice) return null;
                var (characterId, audio) = (voice.CharacterId, voice.AudioFileName);
                var outcome = await mutations.CommitAsync(
                    new DeleteVoiceMutation(book.FolderId, voiceId) { CopiedStep = (label, action) }, CancellationToken.None);
                if (outcome is BookMutationOutcome.Rejected rejected) return rejected.Message;

                // The files go after the commit that stops naming them (ADR 0007).
                var folder = fs.GetProjectFolderPath(book.FolderId);
                if (audio is { Length: > 0 }) TryDelete(Path.Combine(folder, audio));
                TryDelete(Path.Combine(folder, VoiceOriginalStore.PathOf(characterId, voiceId)));
                return null;
            });
            Report(peers, voiceId, results);
            Signal(peers.SeriesId, SeriesChangeKind.Changed);
            return results;
        }

        /// <summary>
        /// A linked Character was deleted from its book: its link goes, and the Series character too
        /// when no book is linked any more. The other books keep their Voices.
        /// </summary>
        public async Task ForgetCharacterAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await PeersCoreAsync(folder, characterId, ct) is not { } peers) return;
            if (await projects.GetProjectAsync(folder) is not { } project) return;
            await using (var db = await Data.Series.SeriesDbContext.OpenAsync(FolderOf(peers.SeriesId), ct))
                await RemoveLinksAsync(db, l => l.ProjectId == project.Id && l.CharacterId == characterId, ct);
            Signal(peers.SeriesId, SeriesChangeKind.Changed);
        }

        /// <summary>A member book committed something the series cast board shows: its series reads again (#213, Hub).</summary>
        public async Task SignalMemberAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            if (await MembershipOfAsync(folder, ct) is { } series)
                Signal(series.SeriesId, SeriesChangeKind.Changed);
        }

        // ---- internals ---------------------------------------------------------------------------

        private async Task<SeriesPeers?> PeersCoreAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct)
        {
            if (await projects.GetProjectAsync(folder) is not { } project) return null;
            var series = (await LoadAllAsync(ct)).All.FirstOrDefault(s => s.Members.ContainsKey(project.Id));
            if (series is null) return null;
            var links = await LoadLinksAsync(series.Id, ct);
            var mine = links.FirstOrDefault(l => l.ProjectId == project.Id && l.CharacterId == characterId);
            if (mine is null) return null;

            var index = await IndexProjectsAsync();
            var self = new SeriesPeer(folder.Value, index.Title(project.Id), SeriesPositions.Normalize(series.Members[project.Id]), characterId);
            var others = links
                .Where(l => l.SeriesCharacterId == mine.SeriesCharacterId && l.ProjectId != project.Id && series.Members.ContainsKey(l.ProjectId))
                .Select(l => index.ById(l.ProjectId) is { } p
                    ? new SeriesPeer(p.Folder, p.Title, SeriesPositions.Normalize(series.Members[l.ProjectId]), l.CharacterId)
                    : null)
                .OfType<SeriesPeer>()
                .OrderBy(p => p.Position)
                .ToList();
            return new SeriesPeers(series.Id, mine.SeriesCharacterId, self, others);
        }

        /// <summary>
        /// Each book with its positional rules that use the Voice, and what it holds:
        /// <paramref name="voicesOf"/> answers a book's Voice Ids.
        /// </summary>
        private async Task<IReadOnlyList<SeriesVoiceDeleteRules.OtherBook>> DeleteUsesAsync(
            IReadOnlyList<SeriesPeer> books, Guid voiceId, Func<SeriesPeer, Task<IReadOnlyCollection<Guid>>> voicesOf)
        {
            var uses = new List<SeriesVoiceDeleteRules.OtherBook>();
            foreach (var book in books)
            {
                var rules = await characters.GetCharacterVoiceRulesAsync(book.FolderId, book.CharacterId);
                var held = await voicesOf(book);
                uses.Add(new SeriesVoiceDeleteRules.OtherBook(
                    book.Folder, book.Title, rules.Count(r => !r.IsDefault && r.VoiceId == voiceId),
                    held.Contains(voiceId), held.Any(id => id != voiceId)));
            }
            return uses;
        }

        private async Task<IReadOnlyCollection<Guid>> VoiceIdsAsync(SeriesPeer book) =>
            (await characters.GetCharacterVoicesAsync(book.FolderId, book.CharacterId)).Select(v => v.Id).ToList();

        /// <summary>
        /// One book's copy: the reference audio and kept original written over this book's (its own
        /// set aside, and put back if the commit doesn't take them), then the row. Answers why it
        /// failed, or null.
        /// </summary>
        private async Task<string?> CopyIntoAsync(
            SeriesPeer book, ProjectFolderId source, Guid sourceCharacterId, SeriesVoiceCopy copy, string label, StepAction action)
        {
            var targetFolder = fs.GetProjectFolderPath(book.FolderId);
            var previous = (await characters.GetVoiceAsync(book.FolderId, copy.Id))?.AudioFileName;
            var staged = new StagedFiles(logger);
            try
            {
                var (audio, failure) = StageVoice(staged, source, sourceCharacterId, book, copy);
                if (failure is not null) return failure;

                var outcome = await mutations.CommitAsync(
                    new CopySeriesVoiceMutation(book.FolderId, book.CharacterId, copy with { AudioFileName = audio }, label, action),
                    CancellationToken.None);
                if (outcome is BookMutationOutcome.Rejected rejected)
                {
                    staged.Rollback();
                    return rejected.Message;
                }

                staged.Commit();
                // The audio this book named before, under another name, is no longer named: it goes after the commit.
                if (previous is { Length: > 0 } && !string.Equals(previous, audio, StringComparison.OrdinalIgnoreCase))
                    TryDelete(Path.Combine(targetFolder, previous));
                return null;
            }
            catch
            {
                staged.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Writes one Voice's reference audio and kept original from <paramref name="source"/> over
        /// <paramref name="target"/>'s own, under the target Character's folder; with no audio, the
        /// target's kept original is set aside. Answers the audio path in the target, or why it failed.
        /// </summary>
        private (string? Audio, string? Failure) StageVoice(
            StagedFiles staged, ProjectFolderId source, Guid sourceCharacterId, SeriesPeer target, SeriesVoiceCopy copy)
        {
            var sourceFolder = fs.GetProjectFolderPath(source);
            var targetFolder = fs.GetProjectFolderPath(target.FolderId);
            var original = Path.Combine(targetFolder, VoiceOriginalStore.PathOf(target.CharacterId, copy.Id));
            if (copy.AudioFileName is not { Length: > 0 } from)
            {
                staged.Remove(original);
                return (null, null);
            }

            var sourceAudio = Path.Combine(sourceFolder, from);
            if (!File.Exists(sourceAudio)) return (null, $"the reference audio '{from}' is missing.");
            var audio = $"voices/{target.CharacterId}/{Path.GetFileName(from)}";
            staged.Write(sourceAudio, Path.Combine(targetFolder, audio));

            var sourceOriginal = Path.Combine(sourceFolder, VoiceOriginalStore.PathOf(sourceCharacterId, copy.Id));
            if (File.Exists(sourceOriginal)) staged.Write(sourceOriginal, original);
            else staged.Remove(original);
            return (audio, null);
        }

        private void Report(SeriesPeers peers, Guid voiceId, IReadOnlyList<SeriesCopyResult> results)
        {
            foreach (var failed in results.Where(r => r.Failure is not null))
                logger.LogWarning("Series {SeriesId}: Voice {VoiceId} was not copied into '{Folder}': {Reason}",
                    peers.SeriesId, voiceId, failed.Folder, failed.Failure);
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
                File.Copy(from, to);
                _written.Add(to);
            }

            public void Remove(string path) => SetAside(path);

            private void SetAside(string path)
            {
                if (!File.Exists(path)) return;
                File.Move(path, path + BackupSuffix, overwrite: true);
                _setAside.Add(path);
            }

            public void Commit()
            {
                foreach (var path in _setAside)
                    Try(() => File.Delete(path + BackupSuffix), path);
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

    /// <summary>
    /// The inline series Voice editor's read: the series copy's book, Character and Voices, each
    /// linked book in position order, and the refused deletes by Voice.
    /// </summary>
    public sealed record SeriesCharacterView(
        string SeriesCopyFolder,
        Guid SeriesCopyCharacterId,
        IReadOnlyList<Data.Entities.Voice> Voices,
        IReadOnlyList<SeriesCharacterBook> Books,
        IReadOnlyDictionary<Guid, SeriesVoiceDeleteBlock> DeleteBlocks);

    /// <summary>A linked book in the editor's read; <see cref="Differs"/>: its Voices are not the series copy.</summary>
    public sealed record SeriesCharacterBook(
        string Folder, string Title, decimal Position, Guid CharacterId, string CharacterName, Guid? DefaultVoiceId, bool Differs);
}
