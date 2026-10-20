using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// The voice audio editor's apply and restore. Both rewrite a Voice's reference audio, which is
    /// Project state, so both commit as Book mutations: open views hear about it from the receipt,
    /// and a write the Book refuses leaves the audio as it was. The write side is real for that
    /// reason — "the receipt went out and the file is the new one" is a claim about both at once.
    /// </summary>
    public class VoiceAudioEditorTests : ProjectDbTestBase
    {
        private static readonly Guid AliceId = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private readonly FakeFileSystem _fs;
        private readonly ProjectFolderId _folder;
        private readonly VoiceOriginalStore _originals;
        private readonly List<BookMutationReceipt> _receipts = [];

        public VoiceAudioEditorTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>().Event += _receipts.Add;

            _folder = new ProjectFolderId(FolderName);
            _fs = new FakeFileSystem(TempDir);
            _fs.SeedFolder(FolderName);
            _originals = new VoiceOriginalStore(_fs);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private VoiceAudioEditor Sut()
        {
            var scope = _root.CreateScope();
            return new VoiceAudioEditor(
                _originals,
                _fs,
                scope.ServiceProvider.GetRequiredService<BookMutations>(),
                NullLogger<VoiceAudioEditor>.Instance);
        }

        /// <summary>A Voice whose reference audio is <c>[1, 2, 3]</c>, and the editor's handle on it.</summary>
        private async Task<VoiceAudioRef> SeedVoiceAsync()
        {
            await new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" })
                .BuildAsync();

            await using var scope = _root.CreateAsyncScope();
            var mutations = scope.ServiceProvider.GetRequiredService<BookMutations>();
            var created = await mutations.CommitAsync(new CreateVoiceMutation(_folder, AliceId, "Alice Voice"));
            var voiceId = Assert.IsType<BookMutationOutcome.Committed>(created).Receipt.Effects.CreatedId!.Value;

            var live = $"voices/{AliceId}/{voiceId}-alice-voice.wav";
            await mutations.CommitAsync(new SetVoiceAudioMutation(_folder, voiceId, live));
            _fs.SeedFile(FullPath(live), [1, 2, 3]);

            _receipts.Clear();
            return new VoiceAudioRef(_folder, AliceId, voiceId, live);
        }

        /// <summary>A Voice the Book does not have, whose audio file is nonetheless on disk.</summary>
        private VoiceAudioRef MissingVoice()
        {
            var voiceId = Guid.NewGuid();
            var live = $"voices/{AliceId}/{voiceId}-gone.wav";
            _fs.SeedFile(FullPath(live), [1, 2, 3]);
            return new VoiceAudioRef(_folder, AliceId, voiceId, live);
        }

        private string FullPath(string relativePath) =>
            Path.Combine(_fs.GetProjectFolderPath(FolderName), relativePath.Replace('/', Path.DirectorySeparatorChar));

        private byte[] Live(VoiceAudioRef voice) => _fs.GetFileContent(FullPath(voice.LiveRelativePath));

        // ── apply ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Apply_CapturesTheOriginal_WritesOverTheExistingPath_AndPublishesAReceipt()
        {
            var voice = await SeedVoiceAsync();

            await Sut().ApplyAsync(voice, [7, 7]);

            // Same path as before: Apply never re-derives the name (a rename would orphan the file).
            Assert.Equal([7, 7], Live(voice));
            Assert.Equal([1, 2, 3], await _originals.TryReadAsync(_folder, AliceId, voice.VoiceId));
            var receipt = Assert.Single(_receipts);
            Assert.Equal(nameof(ApplyVoiceAudioEditMutation), receipt.MutationName);
            Assert.Equal(BookFacets.Voices, receipt.Effects.Facets);
        }

        [Fact]
        public async Task SecondApply_KeepsTheFirstOriginal()
        {
            var voice = await SeedVoiceAsync();

            await Sut().ApplyAsync(voice, [7, 7]);
            await Sut().ApplyAsync(voice, [8, 8]);

            Assert.Equal([1, 2, 3], await _originals.TryReadAsync(_folder, AliceId, voice.VoiceId));
            Assert.Equal([8, 8], Live(voice));
        }

        /// <summary>
        /// A refused write must not leave an edit nobody recorded: the live audio goes back to what it
        /// was, and an original captured for this Apply goes with it — its existence is the claim
        /// that the audio has been edited.
        /// </summary>
        [Fact]
        public async Task Apply_WhenTheVoiceIsGone_ThrowsAndLeavesTheAudioAsItWas()
        {
            var voice = MissingVoice();

            await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().ApplyAsync(voice, [7, 7]));

            Assert.Equal([1, 2, 3], Live(voice));
            Assert.False(_originals.Exists(_folder, AliceId, voice.VoiceId));
            Assert.Empty(_receipts);
        }

        // ── restore ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Restore_CopiesTheOriginalBack_DeletesIt_AndPublishesAReceipt()
        {
            var voice = await SeedVoiceAsync();
            await Sut().ApplyAsync(voice, [7, 7]);
            _receipts.Clear();

            await Sut().RestoreOriginalAsync(voice);

            Assert.Equal([1, 2, 3], Live(voice));
            // The delete is what keeps the invariant exact: a surviving copy leaves the chip lying.
            Assert.False(_originals.Exists(_folder, AliceId, voice.VoiceId));
            var receipt = Assert.Single(_receipts);
            Assert.Equal(nameof(RestoreVoiceAudioOriginalMutation), receipt.MutationName);
            Assert.Equal(BookFacets.Voices, receipt.Effects.Facets);
        }

        [Fact]
        public async Task Restore_WithNoOriginal_IsANoOp()
        {
            var voice = await SeedVoiceAsync();

            await Sut().RestoreOriginalAsync(voice);

            Assert.Equal([1, 2, 3], Live(voice));
            Assert.Empty(_receipts);
        }
    }
}
