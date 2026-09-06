using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Audio;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// Both halves of the Voice audio ordering rule (ADR 0007), which is one claim read in two
    /// directions: the persisted Book never names a file that is not there, and never stops naming
    /// one that still is.
    /// <para>
    /// Audio that is arriving is stored <em>before</em> its mutation, so a receipt cannot reach a
    /// reader ahead of the take it names, and a write that does not commit takes the staged file away
    /// again. Audio that is leaving goes <em>after</em>, so a delete that does not commit — a
    /// cancelled batch step is enough — cannot leave a Voice naming audio that is gone.
    /// </para>
    /// <para>
    /// The write side is real — a SQLite project and <see cref="BookMutations"/> — because "the Voice
    /// names the file that is actually there" is a claim about both at once. What each mutation
    /// reports is asserted in <c>VoiceLifecycleMutationTests</c>.
    /// </para>
    /// </summary>
    public class VoiceAudioWriterTests : ProjectDbTestBase
    {
        private static readonly Guid AliceId = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private readonly FakeFileSystem _fs;
        private readonly ProjectFolderId _folder;
        private readonly StubAudioPipeline _pipeline = new();

        public VoiceAudioWriterTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();

            _folder = new ProjectFolderId(FolderName);
            _fs = new FakeFileSystem(TempDir);
            _fs.SeedFolder(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>
        /// Stands in for the audio pipeline: it writes the bytes where the real one would and hands
        /// back the same project-relative path, without ffmpeg in the way.
        /// </summary>
        private sealed class StubAudioPipeline : IAudioPipeline
        {
            public Func<AudioStoreRequest, string> Store { get; set; } =
                r => $"voices/{r.CharacterId}/{r.VoiceId}-v.wav";

            public Task<string> StoreAsync(AudioStoreRequest request, CancellationToken ct = default) =>
                Task.FromResult(Store(request));

            public Task<string> StoreParagraphAudioAsync(
                ProjectFolderId folderId, Guid paragraphItemId, Stream source, CancellationToken ct = default) =>
                throw new NotSupportedException();
        }

        private VoiceAudioWriter Sut()
        {
            var scope = _root.CreateScope();
            return new VoiceAudioWriter(
                _pipeline,
                scope.ServiceProvider.GetRequiredService<ICharacterReader>(),
                _fs,
                scope.ServiceProvider.GetRequiredService<BookMutations>(),
                NullLogger<VoiceAudioWriter>.Instance);
        }

        private VoiceAudioRemover Remover()
        {
            var scope = _root.CreateScope();
            return new VoiceAudioRemover(
                scope.ServiceProvider.GetRequiredService<ICharacterReader>(),
                _fs,
                new VoiceOriginalStore(_fs),
                scope.ServiceProvider.GetRequiredService<BookMutations>(),
                NullLogger<VoiceAudioRemover>.Instance);
        }

        private async Task<Guid> SeedVoiceAsync()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .BuildAsync();

            await using var scope = _root.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<BookMutations>()
                .CommitAsync(new CreateVoiceMutation(_folder, AliceId, "Alice Voice"));
            return Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects.CreatedId!.Value;
        }

        private AudioStoreRequest RequestFor(Guid voiceId) => new()
        {
            FolderId = _folder,
            CharacterId = AliceId,
            CharacterName = "Alice",
            CharacterAliases = [],
            VoiceId = voiceId,
            VoiceName = "Alice Voice",
            Source = new MemoryStream([0x52, 0x49, 0x46, 0x46]),
            Extension = ".wav",
        };

        /// <summary>Puts the bytes where the pipeline would have, so the staged file actually exists.</summary>
        private string Stage(string relativePath)
        {
            _fs.SeedFile(FullPath(relativePath), [0x52, 0x49, 0x46, 0x46]);
            return relativePath;
        }

        private string FullPath(string relativePath) =>
            Path.Combine(
                _fs.GetProjectFolderPath(FolderName),
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        private async Task<string?> ReadAudioFileNameAsync(Guid voiceId)
        {
            await using var db = await OpenDbAsync();
            return (await db.Voices.FirstAsync(v => v.Id == voiceId)).AudioFileName;
        }

        // ── the committed path ───────────────────────────────────────────────

        [Fact]
        public async Task RecordUploaded_NamesTheStoredFileOnTheVoice()
        {
            var voiceId = await SeedVoiceAsync();
            _pipeline.Store = r => Stage($"voices/{r.CharacterId}/{r.VoiceId}-v.wav");

            var path = await Sut().RecordUploadedAsync(RequestFor(voiceId));

            Assert.Equal($"voices/{AliceId}/{voiceId}-v.wav", path);
            Assert.Equal(path, await ReadAudioFileNameAsync(voiceId));
            Assert.True(_fs.FileExists(FullPath(path)));
        }

        [Fact]
        public async Task RecordGenerated_StoresTheTranscriptAndPromptWithTheAudio()
        {
            var voiceId = await SeedVoiceAsync();
            _pipeline.Store = r => Stage($"voices/{r.CharacterId}/{r.VoiceId}-v.wav");

            var path = await Sut().RecordGeneratedAsync(RequestFor(voiceId), "sample text", "a warm voice");

            await using var db = await OpenDbAsync();
            var voice = await db.Voices.FirstAsync(v => v.Id == voiceId);
            Assert.Equal(path, voice.AudioFileName);
            Assert.Equal("sample text", voice.Transcript);
            Assert.Equal("a warm voice", voice.DesignPrompt);
        }

        // ── the uncommitted path ─────────────────────────────────────────────

        /// <summary>
        /// Until the mutation commits nothing in the Book names the file, so a refused write must take
        /// it away — otherwise every failed upload leaves an artifact behind that nobody can find and
        /// nothing will clean up.
        /// </summary>
        [Fact]
        public async Task RecordUploaded_WhenTheVoiceIsGone_ThrowsAndRemovesTheStagedFile()
        {
            var missing = Guid.NewGuid();
            var relativePath = $"voices/{AliceId}/{missing}-v.wav";
            _pipeline.Store = _ => Stage(relativePath);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Sut().RecordUploadedAsync(RequestFor(missing)));

            Assert.False(_fs.FileExists(FullPath(relativePath)));
        }

        /// <summary>
        /// The one file the cleanup will not take: a replacement lands at the same id-and-name derived
        /// path as the take it replaces, so after a refused write the Voice still names it. Removing
        /// it would leave the row pointing at nothing, which is worse than leaving it pointing at the
        /// audio it named a moment ago.
        /// <para>
        /// Cancellation is the refusal used here because it is the one that leaves the Voice exactly
        /// as it was — a Voice the Book no longer has names no path, and the file it staged is then
        /// rightly taken away.
        /// </para>
        /// </summary>
        [Fact]
        public async Task RecordUploaded_Cancelled_KeepsTheFileTheVoiceStillNames()
        {
            var voiceId = await SeedVoiceAsync();
            var relativePath = $"voices/{AliceId}/{voiceId}-v.wav";
            _pipeline.Store = _ => Stage(relativePath);

            // The first record commits and leaves the Voice naming this path.
            await Sut().RecordUploadedAsync(RequestFor(voiceId));

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => Sut().RecordUploadedAsync(RequestFor(voiceId), new CancellationToken(canceled: true)));

            Assert.True(_fs.FileExists(FullPath(relativePath)));
            Assert.Equal(relativePath, await ReadAudioFileNameAsync(voiceId));
        }

        // ── audio that is leaving ────────────────────────────────────────────

        [Fact]
        public async Task DeleteVoice_DropsTheVoiceThenItsAudioAndStoredOriginal()
        {
            var voiceId = await SeedVoiceAsync();
            var (audio, original) = await SeedVoiceAudioAsync(voiceId);

            var outcome = await Remover().DeleteVoiceAsync(_folder, voiceId);

            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            await using var db = await OpenDbAsync();
            Assert.False(await db.Voices.AnyAsync(v => v.Id == voiceId));
            Assert.False(_fs.FileExists(audio));
            Assert.False(_fs.FileExists(original));
        }

        [Fact]
        public async Task SetVoiceSource_ToGenerated_DropsTheRecordingAndItsStoredOriginal()
        {
            var voiceId = await SeedVoiceAsync();
            var (audio, original) = await SeedVoiceAudioAsync(voiceId);

            var outcome = await Remover().SetVoiceSourceAsync(_folder, voiceId, isGenerated: true);

            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            Assert.Null(await ReadAudioFileNameAsync(voiceId));
            Assert.False(_fs.FileExists(audio));
            Assert.False(_fs.FileExists(original));
        }

        /// <summary>
        /// The direction the ordering rule exists for. A cancelled delete commits nothing, so the
        /// Voice is still there — and it must still have the audio it names, or the Characters tab
        /// shows a Voice whose recording 404s and the Audio Queue cannot speak it.
        /// </summary>
        [Fact]
        public async Task DeleteVoice_Cancelled_KeepsBothTheVoiceAndItsAudio()
        {
            var voiceId = await SeedVoiceAsync();
            var (audio, original) = await SeedVoiceAudioAsync(voiceId);

            var outcome = await Remover().DeleteVoiceAsync(
                _folder, voiceId, new CancellationToken(canceled: true));

            Assert.Equal(BookMutationRejection.Cancelled,
                Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
            await using var db = await OpenDbAsync();
            Assert.True(await db.Voices.AnyAsync(v => v.Id == voiceId));
            Assert.True(_fs.FileExists(audio));
            Assert.True(_fs.FileExists(original));
        }

        /// <summary>
        /// Made uploaded, a Voice loses its design prompt and keeps its recording — so nothing here
        /// touches a file, whatever the outcome.
        /// </summary>
        [Fact]
        public async Task SetVoiceSource_ToUploaded_KeepsTheRecording()
        {
            var voiceId = await SeedVoiceAsync();
            var (audio, _) = await SeedVoiceAudioAsync(voiceId);
            await Remover().SetVoiceSourceAsync(_folder, voiceId, isGenerated: true);
            _pipeline.Store = _ => Stage($"voices/{AliceId}/{voiceId}-v.wav");
            await Sut().RecordUploadedAsync(RequestFor(voiceId));

            await Remover().SetVoiceSourceAsync(_folder, voiceId, isGenerated: false);

            Assert.True(_fs.FileExists(audio));
            Assert.NotNull(await ReadAudioFileNameAsync(voiceId));
        }

        /// <summary>Gives a Voice a live WAV, the stored original that marks it edited, and the row pointing at both.</summary>
        private async Task<(string Audio, string Original)> SeedVoiceAudioAsync(Guid voiceId)
        {
            var relativePath = $"voices/{AliceId}/{voiceId}-v.wav";
            var original = $"voices/{AliceId}/{voiceId}.orig.wav";
            Stage(relativePath);
            Stage(original);

            await using var scope = _root.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<BookMutations>()
                .CommitAsync(new SetVoiceAudioMutation(_folder, voiceId, relativePath));

            return (FullPath(relativePath), FullPath(original));
        }
    }
}
