#pragma warning disable BL0005 // Component parameter set outside component — intentional in tests
using MudBlazor;
using NSubstitute;
using Read2Me.App.Services;
using Read2Me.App.Shared.Characters;
using Read2Me.App.State;
using Read2Me.AppData.Entities;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Voice;
using Xunit;

namespace Read2Me.Tests.App.Characters
{
    public class CharacterDetailPanelTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private sealed class FakeTranscriptionSettings : TranscriptionSettingsService
        {
            public FakeTranscriptionSettings() : base(null!, null!) { }
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult<TranscriptionServiceConfig?>(null);
        }

        private sealed class FakeVoiceDesignPromptService : VoiceDesignPromptService
        {
            private readonly string? _buildResult;
            private readonly VoiceDesignPromptService.GenerateResult _generateResult;

            public FakeVoiceDesignPromptService(
                string? buildResult,
                VoiceDesignPromptService.GenerateResult generateResult)
                : base(null!, null!, null!, null!)
            {
                _buildResult = buildResult;
                _generateResult = generateResult;
            }

            public override Task<string> BuildRenderedPromptAsync(
                string bookTitle, string author, string characterName) =>
                Task.FromResult(_buildResult ?? "");

            public override Task<VoiceDesignPromptService.GenerateResult> GenerateWithPromptAsync(
                string renderedPrompt, CancellationToken ct = default) =>
                Task.FromResult(_generateResult);
        }

        private static CharacterPresenter BuildPresenter(
            Guid characterId,
            string? buildResult,
            VoiceDesignPromptService.GenerateResult generateResult)
        {
            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Folder)
                .Returns(new List<Character> { new() { Id = characterId, Name = "Alice" } });
            reader.GetProjectAsync(Folder)
                .Returns(new Project { BookTitle = "Dune", Author = "Herbert" });
            reader.GetCharacterLinesAsync(Folder, characterId)
                .Returns(new List<CharacterLine>());
            reader.GetCharacterVoicesAsync(Folder, characterId)
                .Returns(new List<Voice>());

            var orchestrator = new VoiceOrchestrator(
                audioPipeline: Substitute.For<IAudioPipeline>(),
                transcriptionResolver: Substitute.For<ITranscriptionClientResolver>(),
                voiceAudioGenerator: Substitute.For<IVoiceAudioGenerator>(),
                transcriptionSettings: new FakeTranscriptionSettings(),
                voiceDesignPromptService: new FakeVoiceDesignPromptService(buildResult, generateResult),
                fileSystem: Substitute.For<IFileSystem>());

            return new CharacterPresenter(reader, Substitute.For<IBookCommandHandler>(), orchestrator);
        }

        private static CharacterDetailPanel CreatePanel(CharacterPresenter presenter, Guid characterId)
        {
            var panel = new CharacterDetailPanel
            {
                Character = new Character { Id = characterId, Name = "Alice" },
                Lines = [],
                AllCharacters = [],
                FolderName = "test-book",
                Presenter = presenter,
            };
            return panel;
        }

        [Fact]
        public async Task RegeneratePrompt_BuildReturnsNull_DoesNotOpenDialog()
        {
            var characterId = Guid.NewGuid();
            var presenter = BuildPresenter(
                characterId,
                buildResult: null,
                generateResult: new VoiceDesignPromptService.GenerateResult(
                    VoiceDesignPromptService.GenerateStatus.Failed, null, null));

            // Presenter has no active folder -> BuildDesignPromptAsync returns null
            // (we do NOT call LoadAsync, so _folderId is null)
            var panel = CreatePanel(presenter, characterId);
            var voice = new Voice { Id = Guid.NewGuid() };

            bool dialogOpened = false;
            await panel.RegeneratePromptCoreAsync(
                voice,
                _ => { dialogOpened = true; return Task.FromResult<DialogResult?>(null); });

            Assert.False(dialogOpened);
        }

        [Fact]
        public async Task RegeneratePrompt_UserCancels_DraftNotSet()
        {
            var characterId = Guid.NewGuid();
            var presenter = BuildPresenter(
                characterId,
                buildResult: "rendered prompt",
                generateResult: new VoiceDesignPromptService.GenerateResult(
                    VoiceDesignPromptService.GenerateStatus.Success, "generated", null));

            await presenter.LoadAsync(Folder);
            var panel = CreatePanel(presenter, characterId);
            var voice = new Voice { Id = Guid.NewGuid(), DesignPrompt = null };

            await panel.RegeneratePromptCoreAsync(
                voice,
                _ => Task.FromResult<DialogResult?>(DialogResult.Cancel()));

            Assert.False(panel.HasPromptDraft(voice));
            Assert.Equal("", panel.GetPromptDraft(voice));
        }

        [Fact]
        public async Task RegeneratePrompt_LlmSucceeds_DraftSetToGeneratedPrompt()
        {
            var characterId = Guid.NewGuid();
            var presenter = BuildPresenter(
                characterId,
                buildResult: "rendered prompt",
                generateResult: new VoiceDesignPromptService.GenerateResult(
                    VoiceDesignPromptService.GenerateStatus.Success, "rich voice description", null));

            await presenter.LoadAsync(Folder);
            var panel = CreatePanel(presenter, characterId);
            var voice = new Voice { Id = Guid.NewGuid(), DesignPrompt = null };

            await panel.RegeneratePromptCoreAsync(
                voice,
                prompt => Task.FromResult<DialogResult?>(DialogResult.Ok(prompt)));

            Assert.Equal("rich voice description", panel.GetPromptDraft(voice));
            Assert.True(panel.HasPromptDraft(voice));
        }

        [Fact]
        public async Task RegeneratePrompt_LlmFails_DraftNotSet()
        {
            var characterId = Guid.NewGuid();
            var presenter = BuildPresenter(
                characterId,
                buildResult: "rendered prompt",
                generateResult: new VoiceDesignPromptService.GenerateResult(
                    VoiceDesignPromptService.GenerateStatus.Failed, null, "LLM timeout"));

            await presenter.LoadAsync(Folder);
            var panel = CreatePanel(presenter, characterId);
            var voice = new Voice { Id = Guid.NewGuid(), DesignPrompt = null };

            await panel.RegeneratePromptCoreAsync(
                voice,
                prompt => Task.FromResult<DialogResult?>(DialogResult.Ok(prompt)));

            Assert.False(panel.HasPromptDraft(voice));
        }

        [Fact]
        public async Task RegeneratePrompt_PassesBuiltPromptToDialog()
        {
            var characterId = Guid.NewGuid();
            var presenter = BuildPresenter(
                characterId,
                buildResult: "my rendered prompt",
                generateResult: new VoiceDesignPromptService.GenerateResult(
                    VoiceDesignPromptService.GenerateStatus.Failed, null, null));

            await presenter.LoadAsync(Folder);
            var panel = CreatePanel(presenter, characterId);
            var voice = new Voice { Id = Guid.NewGuid() };

            string? receivedPrompt = null;
            await panel.RegeneratePromptCoreAsync(
                voice,
                p => { receivedPrompt = p; return Task.FromResult<DialogResult?>(DialogResult.Cancel()); });

            Assert.Equal("my rendered prompt", receivedPrompt);
        }
    }
}
