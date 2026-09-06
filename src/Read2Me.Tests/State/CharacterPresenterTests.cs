using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Read2Me.App.Services;
using Read2Me.App.State;
using Read2Me.Core.Audio;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.AppData.Entities;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using Read2Me.Services.Voice;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.State
{
    /// <summary>
    /// The Characters tab's adapter. Its write side is real — a SQLite project and
    /// <see cref="BookMutations"/> — because every gesture on this page is a Book mutation
    /// (ADR 0007), and "the tab shows what was written" is only worth asserting against a Book that
    /// was actually written to.
    /// <para>
    /// Its read side stays substituted: what this page renders is one character's lines, voices and
    /// rules, and the tests below are about what the presenter does with those objects — patching a
    /// loaded Voice in place rather than replacing every one of them — not about the queries that
    /// produce them. What each mutation does to the Book is asserted in
    /// <c>CharacterLifecycleMutationTests</c> and <c>VoiceLifecycleMutationTests</c>.
    /// </para>
    /// </summary>
    public class CharacterPresenterTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _circuit;
        private readonly ProjectFolderId _folder;

        public CharacterPresenterTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _circuit = _root.CreateAsyncScope();
            _folder = new ProjectFolderId(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _circuit.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private sealed class FakeTranscriptionSettings : TranscriptionSettingsService
        {
            public FakeTranscriptionSettings() : base(null!, null!) { }
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult<TranscriptionServiceConfig?>(null);
        }

        private sealed class FakeVoiceDesignPromptService : VoiceDesignPromptService
        {
            public FakeVoiceDesignPromptService() : base(null!, null!, null!, null!) { }
            public override Task<GenerateResult> GenerateWithPromptAsync(
                string renderedPrompt, CancellationToken ct = default) =>
                Task.FromResult(new GenerateResult(GenerateStatus.Failed, null, null));
        }

        // ── fixture ───────────────────────────────────────────────────────────

        private BookMutations Mutations => _circuit.ServiceProvider.GetRequiredService<BookMutations>();

        /// <summary>
        /// The presenter over the real write side, with its reads substituted. The reader defaults
        /// to an empty roster, so a test that needs one arranges it.
        /// </summary>
        private CharacterPresenter CreatePresenter(
            IVoiceAudioWriter? voiceAudio = null,
            EventBroadcaster<LlmStreamEvent>? llmEvents = null,
            IProjectReader? projectReader = null)
        {
            var reader = projectReader ?? Substitute.For<IProjectReader>();
            if (projectReader is null)
                reader.GetCharactersWithAliasesAsync(_folder).Returns(new List<Character>());

            var orchestrator = new VoiceOrchestrator(
                voiceAudio: voiceAudio ?? Substitute.For<IVoiceAudioWriter>(),
                transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                transcriptionSettings: new FakeTranscriptionSettings(),
                voiceDesignPromptService: new FakeVoiceDesignPromptService(),
                fileSystem: Substitute.For<IFileSystem>());

            return new CharacterPresenter(
                reader,
                Mutations,
                new CharacterResolver(reader, Mutations),
                // Nothing here deletes a Voice or flips its source; those two order a file against
                // the commit, and that ordering is asserted in VoiceAudioWriterTests.
                Substitute.For<IVoiceAudioRemover>(),
                orchestrator,
                llmEvents ?? new EventBroadcaster<LlmStreamEvent>());
        }

        /// <summary>
        /// The smallest Book a Character gesture can be committed against. Each character is seeded
        /// as a row of its own identity and name, never the test's object: what Voices that object
        /// carries is the arrangement of what the page has loaded, and several tests deliberately
        /// load Voices the Book does not have — or two objects for one id.
        /// </summary>
        private Task SeedProjectAsync(params Character[] characters)
        {
            var builder = new BookHierarchyBuilder(OpenDbAsync);
            foreach (var c in characters)
                builder.WithCharacter(c.Name, new Character { Id = c.Id, Name = c.Name });
            return builder.BuildAsync();
        }

        /// <summary>
        /// Puts real Voice rows behind the ids a test's substituted reader hands back. Ids the
        /// character's own navigation collection already carried into the Book are skipped: the
        /// arrangement decides which lists hold a Voice, not how many rows it has.
        /// </summary>
        private async Task SeedVoicesAsync(Guid characterId, params Guid[] voiceIds)
        {
            await using var db = await OpenDbAsync();
            foreach (var id in voiceIds)
                if (await db.Voices.FindAsync(id) is null)
                    db.Voices.Add(new VoiceEntity { Id = id, CharacterId = characterId, Name = "Voice" });
            await db.SaveChangesAsync();
        }

        private async Task<VoiceEntity> PersistedVoiceAsync(Guid voiceId)
        {
            await using var db = await OpenDbAsync();
            return await db.Voices.SingleAsync(v => v.Id == voiceId);
        }

        // ── narrator ──────────────────────────────────────────────────────────

        [Fact]
        public async Task SetNarratorCharacter_LinksTheNarratorInThePersistedBook()
        {
            var watson = new Character { Id = Guid.NewGuid(), Name = "Dr. Watson" };
            await SeedProjectAsync(watson);

            var identity = new NarratorIdentity(watson.Id, watson.Name, true);
            var reader = Substitute.For<IProjectReader>();
            reader.GetNarratorAsync(_folder, Arg.Any<CancellationToken>()).Returns(identity);
            reader.GetCharactersWithAliasesAsync(_folder).Returns(new List<Character> { watson });

            var presenter = CreatePresenter(projectReader: reader);
            await presenter.LoadAsync(_folder);
            await presenter.SetNarratorCharacterAsync(watson.Id);

            Assert.Null(presenter.Error);
            Assert.Equal(identity, presenter.Narrator);

            await using var verify = await OpenDbAsync();
            Assert.Equal(watson.Id, (await NarratorIdentity.LoadAsync(verify)).CharacterId);
        }

        /// <summary>
        /// The refusals the command endpoint softens to a success-shaped null are not softened here:
        /// nothing on this page has to answer a contract that predates them, so a gesture that did
        /// nothing says why.
        /// </summary>
        [Fact]
        public async Task AGestureTheBookRefuses_ReportsWhyRatherThanLookingLikeItWorked()
        {
            await SeedProjectAsync();
            var presenter = CreatePresenter();
            await presenter.LoadAsync(_folder);

            await presenter.DeleteCharacterAsync(ProjectDbContext.NarratorId);

            Assert.NotNull(presenter.Error);

            await using var verify = await OpenDbAsync();
            Assert.True(await verify.Characters.AnyAsync(c => c.Id == ProjectDbContext.NarratorId));
        }

        // ── voice design prompt ───────────────────────────────────────────────

        [Fact]
        public async Task GenerateDesignPrompt_LlmFails_StillBracketsAThroughputRunOfOne()
        {
            var stream = new EventBroadcaster<LlmStreamEvent>();
            var runs = new List<LlmStreamEvent>();
            stream.Event += e => { if (e is RunStarted or RunEnded) runs.Add(e); };

            // The fake design service always fails, so this also covers the rule that a failed
            // run must still close — an unclosed run strands the next run's total.
            var presenter = CreatePresenter(llmEvents: stream);
            await presenter.LoadAsync(_folder);

            Assert.Null(await presenter.GenerateDesignPromptWithTextAsync("rendered prompt"));

            Assert.Collection(runs,
                e => Assert.IsType<RunStarted>(e),
                e => Assert.IsType<RunEnded>(e));
        }

        // ── uploaded voice audio ──────────────────────────────────────────────

        [Fact]
        public async Task VoiceAudioUrl_ChangesAfterUpload()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var recorder = Substitute.For<IVoiceAudioWriter>();
            recorder.RecordUploadedAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/test.wav");

            var presenter = CreatePresenter(voiceAudio: recorder);
            await presenter.LoadAsync(_folder);

            var tokenBefore = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(
                charId, voiceId, "TestVoice",
                new MemoryStream(new byte[] { 1, 2, 3 }), ".wav");
            var tokenAfter = presenter.AudioToken(voiceId);

            Assert.True(tokenAfter > tokenBefore, "AudioToken must increment after upload so URL changes");
        }

        [Fact]
        public async Task VoiceAudioUrl_TwoSuccessiveUploads_TokenIncreasesTwice()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var recorder = Substitute.For<IVoiceAudioWriter>();
            recorder.RecordUploadedAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/test.wav");

            var presenter = CreatePresenter(voiceAudio: recorder);
            await presenter.LoadAsync(_folder);

            var t0 = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "V", new MemoryStream([1]), ".wav");
            var t1 = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "V", new MemoryStream([2]), ".wav");
            var t2 = presenter.AudioToken(voiceId);

            Assert.True(t1 > t0);
            Assert.True(t2 > t1);
        }

        [Fact]
        public async Task UploadVoiceAudio_Success_ErrorIsNullAndTokenBumped()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var recorder = Substitute.For<IVoiceAudioWriter>();
            recorder.RecordUploadedAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/uploaded.wav");

            var presenter = CreatePresenter(voiceAudio: recorder);
            await presenter.LoadAsync(_folder);

            var tokenBefore = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "Voice", new MemoryStream([1, 2, 3]), ".wav");

            Assert.Null(presenter.Error);
            Assert.True(presenter.AudioToken(voiceId) > tokenBefore);
        }

        [Fact]
        public async Task UploadVoiceAudio_OrchestratorThrows_ErrorSetAndTokenNotBumped()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var recorder = Substitute.For<IVoiceAudioWriter>();
            recorder.RecordUploadedAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new IOException("upload failed"));

            var presenter = CreatePresenter(voiceAudio: recorder);
            await presenter.LoadAsync(_folder);

            var tokenBefore = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "Voice", new MemoryStream([1]), ".wav");

            Assert.Equal("upload failed", presenter.Error);
            Assert.Equal(tokenBefore, presenter.AudioToken(voiceId));
        }

        // ── UpdateVoiceInPlace guard tests ────────────────────────────────────

        /// <summary>
        /// A presenter showing one character, with real Voice rows behind the ids its substituted
        /// reader hands back — so the one-field gestures below commit for real and then patch the
        /// loaded objects rather than reloading every one of them.
        /// </summary>
        private async Task<CharacterPresenter> CreatePresenterWithCharacterAsync(
            Character character, List<VoiceEntity> voicesList, params Character[] alsoOnTheRoster)
        {
            await SeedProjectAsync([.. new[] { character }.Concat(alsoOnTheRoster)]);
            await SeedVoicesAsync(character.Id, [.. voicesList.Select(v => v.Id).Distinct()]);

            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(_folder)
                .Returns([.. new[] { character }.Concat(alsoOnTheRoster)]);
            reader.GetCharacterLinesAsync(_folder, character.Id).Returns(new List<CharacterLine>());
            reader.GetCharacterVoicesAsync(_folder, character.Id).Returns(voicesList);

            var presenter = CreatePresenter(projectReader: reader);
            await presenter.LoadAsync(_folder);
            await presenter.SelectCharacterAsync(character);
            return presenter;
        }

        [Fact]
        public async Task UpdateVoiceInPlace_WhenVoicesAndSelectedCharacterShareSameObject_MutatesOnce()
        {
            // Same Voice reference in both Voices list and Character.Voices
            var voiceId = Guid.NewGuid();
            var sharedVoice = new VoiceEntity { Id = voiceId, Transcript = "original" };

            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [sharedVoice] };

            // GetCharacterVoicesAsync returns the SAME object that is in Character.Voices
            var presenter = await CreatePresenterWithCharacterAsync(character, [sharedVoice]);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "updated");

            // The shared object's Transcript should be "updated" exactly once
            Assert.Equal("updated", sharedVoice.Transcript);
            // Presenter.Voices and SelectedCharacter.Voices both point to the same object
            Assert.Same(presenter.Voices.Find(v => v.Id == voiceId),
                        presenter.SelectedCharacter!.Voices.FirstOrDefault(v => v.Id == voiceId));
            Assert.Equal("updated", (await PersistedVoiceAsync(voiceId)).Transcript);
        }

        [Fact]
        public async Task UpdateVoiceInPlace_WhenVoiceOnlyInVoicesList_MutatesVoicesList()
        {
            // Voice in Voices list; SelectedCharacter.Voices is empty
            var voiceId = Guid.NewGuid();
            var voice = new VoiceEntity { Id = voiceId, Transcript = "original" };

            // character has no voices in its navigation collection
            var character = new Character { Id = Guid.NewGuid(), Name = "Bob", Voices = [] };

            var presenter = await CreatePresenterWithCharacterAsync(character, [voice]);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "set from list");

            Assert.Equal("set from list", voice.Transcript);
            Assert.Null(presenter.Error);
        }

        [Fact]
        public async Task UpdateVoiceInPlace_WhenBothListsHaveDifferentObjects_MutatesBoth()
        {
            // Different Voice objects for same ID in Voices vs SelectedCharacter.Voices
            var voiceId = Guid.NewGuid();
            var voiceInList = new VoiceEntity { Id = voiceId, Transcript = "list-original" };
            var voiceInChar = new VoiceEntity { Id = voiceId, Transcript = "char-original" };

            var character = new Character { Id = Guid.NewGuid(), Name = "Carol", Voices = [voiceInChar] };

            var presenter = await CreatePresenterWithCharacterAsync(character, [voiceInList]);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "synced");

            // Both objects mutated since they are different references
            Assert.Equal("synced", voiceInList.Transcript);
            Assert.Equal("synced", voiceInChar.Transcript);
        }

        [Fact]
        public async Task UpdateVoiceInPlace_PatchesNonSelectedCharacterVoices()
        {
            var voiceId = Guid.NewGuid();
            var selectedVoice = new VoiceEntity { Id = voiceId, Transcript = "selected-original" };
            var otherVoice = new VoiceEntity { Id = voiceId, Transcript = "other-original" };

            var selectedChar = new Character { Id = Guid.NewGuid(), Name = "Selected", Voices = [selectedVoice] };
            var otherChar = new Character { Id = Guid.NewGuid(), Name = "Other", Voices = [otherVoice] };

            var presenter = await CreatePresenterWithCharacterAsync(selectedChar, [selectedVoice], otherChar);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "patched");

            Assert.Equal("patched", selectedVoice.Transcript);
            Assert.Equal("patched", otherVoice.Transcript);
        }

        /// <summary>
        /// The patch is applied only when the write took: a gesture the Book refused must not leave
        /// the page showing a value nothing was saved for.
        /// </summary>
        [Fact]
        public async Task UpdateVoiceInPlace_WhenTheWriteIsRefused_LeavesTheLoadedVoiceAlone()
        {
            var voice = new VoiceEntity { Id = Guid.NewGuid(), Transcript = "original" };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [voice] };

            // Seeded with the character but not the Voice, so the Book has nothing to write to.
            await SeedProjectAsync(character);
            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(_folder).Returns(new List<Character> { character });
            reader.GetCharacterLinesAsync(_folder, character.Id).Returns(new List<CharacterLine>());
            reader.GetCharacterVoicesAsync(_folder, character.Id).Returns(new List<VoiceEntity> { voice });

            var presenter = CreatePresenter(projectReader: reader);
            await presenter.LoadAsync(_folder);
            await presenter.SelectCharacterAsync(character);

            await presenter.SetVoiceTranscriptDirectAsync(voice.Id, "never saved");

            Assert.Equal("original", voice.Transcript);
            Assert.NotNull(presenter.Error);
        }

        // ── SetVoiceTtsSettingsOverrideAsync ──────────────────────────────────

        [Fact]
        public async Task SetVoiceTtsSettingsOverrideAsync_CommitsTheOverride()
        {
            var voice = new VoiceEntity { Id = Guid.NewGuid() };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [voice] };

            var presenter = await CreatePresenterWithCharacterAsync(character, [voice]);
            await presenter.SetVoiceTtsSettingsOverrideAsync(voice.Id, "{\"cfg_value\":3.5}");

            Assert.Equal("{\"cfg_value\":3.5}", (await PersistedVoiceAsync(voice.Id)).TtsSettingsOverrideJson);
        }

        [Fact]
        public async Task SetVoiceTtsSettingsOverrideAsync_UpdatesVoiceInPlace()
        {
            var voice = new VoiceEntity { Id = Guid.NewGuid(), TtsSettingsOverrideJson = null };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [voice] };

            var presenter = await CreatePresenterWithCharacterAsync(character, [voice]);
            await presenter.SetVoiceTtsSettingsOverrideAsync(voice.Id, "{\"cfg_value\":3.5}");

            Assert.Equal("{\"cfg_value\":3.5}", voice.TtsSettingsOverrideJson);
        }

        // ── voice rules ───────────────────────────────────────────────────────

        [Fact]
        public async Task CreateVoiceRule_CommitsThePositionalRule()
        {
            var voice = new VoiceEntity { Id = Guid.NewGuid() };
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [voice] };

            var presenter = await CreatePresenterWithCharacterAsync(character, [voice]);
            await presenter.CreateVoiceRuleAsync(character.Id, voice.Id, null, null, null, null);

            Assert.Null(presenter.Error);

            await using var verify = await OpenDbAsync();
            var rule = await verify.VoiceRules.SingleAsync(r => r.CharacterId == character.Id);
            Assert.Equal(voice.Id, rule.VoiceId);
            Assert.False(rule.IsDefault);
        }

        // ── ReadyVoiceCount ───────────────────────────────────────────────────

        [Fact]
        public void ReadyVoiceCount_AllReady_ReturnsTotal()
        {
            var character = new Character
            {
                Id = Guid.NewGuid(), Name = "Alice",
                Voices =
                [
                    new VoiceEntity { Id = Guid.NewGuid(), AudioFileName = "voices/a.wav" },
                    new VoiceEntity { Id = Guid.NewGuid(), AudioFileName = "voices/b.wav" },
                ]
            };
            Assert.Equal(2, CharacterPresenter.ReadyVoiceCount(character));
        }

        [Fact]
        public void ReadyVoiceCount_NoneReady_ReturnsZero()
        {
            var character = new Character
            {
                Id = Guid.NewGuid(), Name = "Alice",
                Voices =
                [
                    new VoiceEntity { Id = Guid.NewGuid() },
                    new VoiceEntity { Id = Guid.NewGuid(), AudioFileName = "" },
                ]
            };
            Assert.Equal(0, CharacterPresenter.ReadyVoiceCount(character));
        }

        [Fact]
        public void ReadyVoiceCount_Partial_ReturnsReadyOnly()
        {
            var character = new Character
            {
                Id = Guid.NewGuid(), Name = "Alice",
                Voices =
                [
                    new VoiceEntity { Id = Guid.NewGuid(), AudioFileName = "voices/a.wav" },
                    new VoiceEntity { Id = Guid.NewGuid() },
                ]
            };
            Assert.Equal(1, CharacterPresenter.ReadyVoiceCount(character));
        }

        [Fact]
        public void ReadyVoiceCount_EmptyVoicesList_ReturnsZero() =>
            Assert.Equal(0, CharacterPresenter.ReadyVoiceCount(
                new Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [] }));

        // ── discovery review ──────────────────────────────────────────────────

        /// <summary>The roster the Book actually holds, minus the seed narrator row.</summary>
        private async Task<List<Character>> PersistedRosterAsync()
        {
            await using var db = await OpenDbAsync();
            return await db.Characters
                .Include(c => c.Aliases)
                .Where(c => c.Id != ProjectDbContext.NarratorId)
                .ToListAsync();
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_IncludedRow_CreatesCharacterAndAliases()
        {
            await SeedProjectAsync();
            var presenter = CreatePresenter();
            await presenter.LoadAsync(_folder);

            await presenter.ApplyDiscoveredCharactersAsync(
                [new DiscoveredCharacterRow { Name = "Gandalf", Aliases = ["Mithrandir", "Greyhame"] }]);

            var gandalf = Assert.Single(await PersistedRosterAsync());
            Assert.Equal("Gandalf", gandalf.Name);
            Assert.Equal(["Greyhame", "Mithrandir"], gandalf.Aliases.Select(a => a.Name).Order());
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_ExcludedRow_WritesNothing()
        {
            await SeedProjectAsync();
            var presenter = CreatePresenter();
            await presenter.LoadAsync(_folder);

            await presenter.ApplyDiscoveredCharactersAsync(
                [new DiscoveredCharacterRow { Name = "Bombadil", Included = false }]);

            Assert.Empty(await PersistedRosterAsync());
        }

        /// <summary>
        /// The row the dialog already knew about: the create resolves to whoever answers to the name
        /// rather than making a second of them, and the new alias still lands on them.
        /// </summary>
        [Fact]
        public async Task ApplyDiscoveredCharacters_ExistingCharacterNewAlias_AddsTheAliasToTheExistingCharacter()
        {
            var frodo = new Character { Id = Guid.NewGuid(), Name = "Frodo" };
            await SeedProjectAsync(frodo);

            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(_folder).Returns(new List<Character> { frodo });
            var presenter = CreatePresenter(projectReader: reader);
            await presenter.LoadAsync(_folder);

            await presenter.ApplyDiscoveredCharactersAsync([new DiscoveredCharacterRow
            {
                Name = "Frodo", Aliases = ["Ringbearer"], AlreadyExists = true,
            }]);

            var persisted = Assert.Single(await PersistedRosterAsync());
            Assert.Equal(frodo.Id, persisted.Id);
            Assert.Equal("Ringbearer", Assert.Single(persisted.Aliases).Name);
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_MixedRows_OnlyIncludedApplied()
        {
            await SeedProjectAsync();
            var presenter = CreatePresenter();
            await presenter.LoadAsync(_folder);

            await presenter.ApplyDiscoveredCharactersAsync(
            [
                new DiscoveredCharacterRow { Name = "Sam" },
                new DiscoveredCharacterRow { Name = "Ghost of Christmas Past", Included = false },
            ]);

            Assert.Equal("Sam", Assert.Single(await PersistedRosterAsync()).Name);
        }
    }
}
