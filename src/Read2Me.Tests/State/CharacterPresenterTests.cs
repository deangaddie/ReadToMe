using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Read2Me.App.Services;
using Read2Me.App.State;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Voice;
using Xunit;

namespace Read2Me.Tests.State
{
    public class CharacterPresenterTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private sealed class FakeTranscriptionSettings : TranscriptionSettingsService
        {
            public FakeTranscriptionSettings() : base(null!, null!) { }
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() => Task.FromResult<TranscriptionServiceConfig?>(null);
        }

        private sealed class FakeVoiceDesignPromptService : VoiceDesignPromptService
        {
            public FakeVoiceDesignPromptService() : base(null!, null!, null!, null!) { }
            public override Task<GenerateResult> GenerateWithPromptAsync(string renderedPrompt, CancellationToken ct = default)
                => Task.FromResult(new GenerateResult(GenerateStatus.Failed, null, null));
        }

        private static CharacterPresenter CreatePresenter(
            IAudioPipeline? audioPipeline = null,
            IBookCommandHandler? commandHandler = null)
        {
            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Folder)
                .Returns(new System.Collections.Generic.List<Read2Me.Data.Entities.Character>());

            var pipeline = audioPipeline ?? Substitute.For<IAudioPipeline>();
            var cmd = commandHandler ?? Substitute.For<IBookCommandHandler>();

            var orchestrator = new VoiceOrchestrator(
                audioPipeline: pipeline,
                transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                transcriptionSettings: new FakeTranscriptionSettings(),
                voiceDesignPromptService: new FakeVoiceDesignPromptService(),
                fileSystem: Substitute.For<IFileSystem>());

            return new CharacterPresenter(reader, cmd, orchestrator);
        }

        [Fact]
        public async Task VoiceAudioUrl_ChangesAfterUpload()
        {
            var voiceId = Guid.NewGuid();
            var charId = Guid.NewGuid();

            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/test.wav");

            var presenter = CreatePresenter(audioPipeline: pipeline);
            await presenter.LoadAsync(Folder);

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

            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/test.wav");

            var presenter = CreatePresenter(audioPipeline: pipeline);
            await presenter.LoadAsync(Folder);

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

            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .Returns("voices/uploaded.wav");

            var presenter = CreatePresenter(audioPipeline: pipeline);
            await presenter.LoadAsync(Folder);

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

            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new IOException("upload failed"));

            var presenter = CreatePresenter(audioPipeline: pipeline);
            await presenter.LoadAsync(Folder);

            var tokenBefore = presenter.AudioToken(voiceId);
            await presenter.UploadVoiceAudioAsync(charId, voiceId, "Voice", new MemoryStream([1]), ".wav");

            Assert.Equal("upload failed", presenter.Error);
            Assert.Equal(tokenBefore, presenter.AudioToken(voiceId));
        }

        // ── UpdateVoiceInPlace guard tests ────────────────────────────────────

        // Helper: builds a presenter whose reader returns the given character (with voices)
        // and whose GetCharacterVoicesAsync returns the given voices list.
        private static async Task<CharacterPresenter> CreatePresenterWithCharacterAsync(
            Read2Me.Data.Entities.Character character,
            System.Collections.Generic.List<Read2Me.Data.Entities.Voice> voicesList,
            IBookCommandHandler? commandHandler = null)
        {
            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Folder)
                .Returns(new System.Collections.Generic.List<Read2Me.Data.Entities.Character> { character });
            reader.GetCharacterLinesAsync(Folder, character.Id)
                .Returns(new System.Collections.Generic.List<Read2Me.Core.Models.CharacterLine>());
            reader.GetCharacterVoicesAsync(Folder, character.Id)
                .Returns(voicesList);

            var cmd = commandHandler ?? Substitute.For<IBookCommandHandler>();

            var orchestrator = new VoiceOrchestrator(
                audioPipeline: Substitute.For<IAudioPipeline>(),
                transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                transcriptionSettings: new FakeTranscriptionSettings(),
                voiceDesignPromptService: new FakeVoiceDesignPromptService(),
                fileSystem: Substitute.For<IFileSystem>());

            var presenter = new CharacterPresenter(reader, cmd, orchestrator);
            await presenter.LoadAsync(Folder);
            await presenter.SelectCharacterAsync(character);
            return presenter;
        }

        [Fact]
        public async Task UpdateVoiceInPlace_WhenVoicesAndSelectedCharacterShareSameObject_MutatesOnce()
        {
            // Same Voice reference in both Voices list and Character.Voices
            var voiceId = Guid.NewGuid();
            var sharedVoice = new Read2Me.Data.Entities.Voice { Id = voiceId, Transcript = "original" };

            var character = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(),
                Name = "Alice",
                Voices = new System.Collections.Generic.List<Read2Me.Data.Entities.Voice> { sharedVoice }
            };

            // GetCharacterVoicesAsync returns the SAME object that's in Character.Voices
            var voicesList = new System.Collections.Generic.List<Read2Me.Data.Entities.Voice> { sharedVoice };

            var presenter = await CreatePresenterWithCharacterAsync(character, voicesList);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "updated");

            // The shared object's Transcript should be "updated" exactly once
            Assert.Equal("updated", sharedVoice.Transcript);
            // Presenter.Voices and SelectedCharacter.Voices both point to the same object
            Assert.Same(presenter.Voices.Find(v => v.Id == voiceId),
                        presenter.SelectedCharacter!.Voices.FirstOrDefault(v => v.Id == voiceId));
        }

        [Fact]
        public async Task UpdateVoiceInPlace_WhenVoiceOnlyInVoicesList_MutatesVoicesList()
        {
            // Voice in Voices list; SelectedCharacter.Voices is empty
            var voiceId = Guid.NewGuid();
            var voice = new Read2Me.Data.Entities.Voice { Id = voiceId, Transcript = "original" };

            var character = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(),
                Name = "Bob",
                Voices = [] // character has no voices in its navigation collection
            };

            var voicesList = new System.Collections.Generic.List<Read2Me.Data.Entities.Voice> { voice };

            var presenter = await CreatePresenterWithCharacterAsync(character, voicesList);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "set from list");

            Assert.Equal("set from list", voice.Transcript);
            Assert.Null(presenter.Error);
        }

        [Fact]
        public async Task UpdateVoiceInPlace_WhenBothListsHaveDifferentObjects_MutatesBoth()
        {
            // Different Voice objects for same ID in Voices vs SelectedCharacter.Voices
            var voiceId = Guid.NewGuid();
            var voiceInList = new Read2Me.Data.Entities.Voice { Id = voiceId, Transcript = "list-original" };
            var voiceInChar = new Read2Me.Data.Entities.Voice { Id = voiceId, Transcript = "char-original" };

            var character = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(),
                Name = "Carol",
                Voices = new System.Collections.Generic.List<Read2Me.Data.Entities.Voice> { voiceInChar }
            };

            var voicesList = new System.Collections.Generic.List<Read2Me.Data.Entities.Voice> { voiceInList };

            var presenter = await CreatePresenterWithCharacterAsync(character, voicesList);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "synced");

            // Both objects mutated since they are different references
            Assert.Equal("synced", voiceInList.Transcript);
            Assert.Equal("synced", voiceInChar.Transcript);
        }

        // ── SetVoiceTtsSettingsOverrideAsync ──────────────────────────────────

        [Fact]
        public async Task SetVoiceTtsSettingsOverrideAsync_DispatchesCommand()
        {
            var voiceId = Guid.NewGuid();
            var voice = new Read2Me.Data.Entities.Voice { Id = voiceId };
            var character = new Read2Me.Data.Entities.Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [voice] };
            var cmd = Substitute.For<IBookCommandHandler>();

            var presenter = await CreatePresenterWithCharacterAsync(character, [voice], cmd);
            await presenter.SetVoiceTtsSettingsOverrideAsync(voiceId, "{\"cfg_value\":3.5}");

            await cmd.Received(1).ExecuteAsync(
                Arg.Is<SetVoiceTtsSettingsOverrideCommand>(c => c.VoiceId == voiceId && c.Json == "{\"cfg_value\":3.5}"),
                Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task SetVoiceTtsSettingsOverrideAsync_UpdatesVoiceInPlace()
        {
            var voiceId = Guid.NewGuid();
            var voice = new Read2Me.Data.Entities.Voice { Id = voiceId, TtsSettingsOverrideJson = null };
            var character = new Read2Me.Data.Entities.Character { Id = Guid.NewGuid(), Name = "Alice", Voices = [voice] };

            var presenter = await CreatePresenterWithCharacterAsync(character, [voice]);
            await presenter.SetVoiceTtsSettingsOverrideAsync(voiceId, "{\"cfg_value\":3.5}");

            Assert.Equal("{\"cfg_value\":3.5}", voice.TtsSettingsOverrideJson);
        }

        // ── ReadyVoiceCount ───────────────────────────────────────────────────

        [Fact]
        public void ReadyVoiceCount_AllReady_ReturnsTotal()
        {
            var presenter = CreatePresenter();
            var character = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(), Name = "Alice",
                Voices =
                [
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = "voices/a.wav" },
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = "voices/b.wav" },
                ]
            };
            Assert.Equal(2, presenter.ReadyVoiceCount(character));
        }

        [Fact]
        public void ReadyVoiceCount_NoneReady_ReturnsZero()
        {
            var presenter = CreatePresenter();
            var character = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(), Name = "Bob",
                Voices =
                [
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = null },
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = string.Empty },
                ]
            };
            Assert.Equal(0, presenter.ReadyVoiceCount(character));
        }

        [Fact]
        public void ReadyVoiceCount_Partial_ReturnsReadyOnly()
        {
            var presenter = CreatePresenter();
            var character = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(), Name = "Carol",
                Voices =
                [
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = "voices/a.wav" },
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = null },
                    new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), AudioFileName = string.Empty },
                ]
            };
            Assert.Equal(1, presenter.ReadyVoiceCount(character));
        }

        [Fact]
        public void ReadyVoiceCount_EmptyVoicesList_ReturnsZero()
        {
            var presenter = CreatePresenter();
            var character = new Read2Me.Data.Entities.Character { Id = Guid.NewGuid(), Name = "Dan", Voices = [] };
            Assert.Equal(0, presenter.ReadyVoiceCount(character));
        }

        // ── UpdateVoiceInPlace patches non-selected character voices ──────────

        [Fact]
        public async Task UpdateVoiceInPlace_PatchesNonSelectedCharacterVoices()
        {
            var voiceId = Guid.NewGuid();
            var selectedVoice = new Read2Me.Data.Entities.Voice { Id = voiceId, Transcript = "selected-original" };
            var otherVoice = new Read2Me.Data.Entities.Voice { Id = voiceId, Transcript = "other-original" };

            var selectedChar = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(), Name = "Selected",
                Voices = [selectedVoice]
            };
            var otherChar = new Read2Me.Data.Entities.Character
            {
                Id = Guid.NewGuid(), Name = "Other",
                Voices = [otherVoice]
            };

            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Folder)
                .Returns(new System.Collections.Generic.List<Read2Me.Data.Entities.Character> { selectedChar, otherChar });
            reader.GetCharacterLinesAsync(Folder, selectedChar.Id)
                .Returns(new System.Collections.Generic.List<Read2Me.Core.Models.CharacterLine>());
            reader.GetCharacterVoicesAsync(Folder, selectedChar.Id)
                .Returns(new System.Collections.Generic.List<Read2Me.Data.Entities.Voice> { selectedVoice });

            var orchestrator = new VoiceOrchestrator(
                audioPipeline: Substitute.For<IAudioPipeline>(),
                transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                transcriptionSettings: new FakeTranscriptionSettings(),
                voiceDesignPromptService: new FakeVoiceDesignPromptService(),
                fileSystem: Substitute.For<IFileSystem>());

            var presenter = new CharacterPresenter(reader, Substitute.For<IBookCommandHandler>(), orchestrator);
            await presenter.LoadAsync(Folder);
            await presenter.SelectCharacterAsync(selectedChar);

            await presenter.SetVoiceTranscriptDirectAsync(voiceId, "patched");

            Assert.Equal("patched", selectedVoice.Transcript);
            Assert.Equal("patched", otherVoice.Transcript);
        }

        /// <summary>Records dispatched commands and returns a scripted id for CreateCharacterCommand.</summary>
        private sealed class ScriptedCommandHandler : IBookCommandHandler
        {
            public List<BookCommand> Executed { get; } = [];
            public Func<CreateCharacterCommand, Guid?> ResolveCreateId { get; set; } = _ => Guid.NewGuid();

            public Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
            {
                Executed.Add(command);
                return Task.FromResult(command is CreateCharacterCommand create ? ResolveCreateId(create) : null);
            }
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_IncludedRow_CreatesCharacterAndAliases()
        {
            var handler = new ScriptedCommandHandler();
            var presenter = CreatePresenter(commandHandler: handler);
            await presenter.LoadAsync(Folder);

            var row = new DiscoveredCharacterRow { Name = "Gandalf", Aliases = ["Mithrandir", "Greyhame"] };
            await presenter.ApplyDiscoveredCharactersAsync([row]);

            Assert.Equal(3, handler.Executed.Count);
            var create = Assert.IsType<CreateCharacterCommand>(handler.Executed[0]);
            Assert.Equal("Gandalf", create.Name);
            var alias1 = Assert.IsType<AddCharacterAliasCommand>(handler.Executed[1]);
            Assert.Equal("Mithrandir", alias1.Name);
            var alias2 = Assert.IsType<AddCharacterAliasCommand>(handler.Executed[2]);
            Assert.Equal("Greyhame", alias2.Name);
            Assert.Equal(alias1.CharacterId, alias2.CharacterId);
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_ExcludedRow_ProducesNoCommands()
        {
            var handler = new ScriptedCommandHandler();
            var presenter = CreatePresenter(commandHandler: handler);
            await presenter.LoadAsync(Folder);

            var row = new DiscoveredCharacterRow { Name = "Bombadil", Included = false };
            await presenter.ApplyDiscoveredCharactersAsync([row]);

            Assert.Empty(handler.Executed);
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_ExistingCharacterNewAlias_StillProducesAliasCommand()
        {
            var existingId = Guid.NewGuid();
            var handler = new ScriptedCommandHandler { ResolveCreateId = _ => existingId };
            var presenter = CreatePresenter(commandHandler: handler);
            await presenter.LoadAsync(Folder);

            var row = new DiscoveredCharacterRow
            {
                Name = "Frodo", Aliases = ["Ringbearer"], AlreadyExists = true,
            };
            await presenter.ApplyDiscoveredCharactersAsync([row]);

            Assert.Equal(2, handler.Executed.Count);
            var alias = Assert.IsType<AddCharacterAliasCommand>(handler.Executed[1]);
            Assert.Equal(existingId, alias.CharacterId);
            Assert.Equal("Ringbearer", alias.Name);
        }

        [Fact]
        public async Task ApplyDiscoveredCharacters_MixedRows_OnlyIncludedApplied()
        {
            var handler = new ScriptedCommandHandler();
            var presenter = CreatePresenter(commandHandler: handler);
            await presenter.LoadAsync(Folder);

            var included = new DiscoveredCharacterRow { Name = "Sam" };
            var excluded = new DiscoveredCharacterRow { Name = "Ghost of Christmas Past", Included = false };
            await presenter.ApplyDiscoveredCharactersAsync([included, excluded]);

            var create = Assert.Single(handler.Executed);
            Assert.Equal("Sam", Assert.IsType<CreateCharacterCommand>(create).Name);
        }
    }
}
