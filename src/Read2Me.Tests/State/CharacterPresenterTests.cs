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
    }
}
