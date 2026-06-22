using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NSubstitute;
using Read2Me.App.Shared.Characters;
using Read2Me.App.Services;
using Read2Me.App.State;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.AppData.Entities;
using Read2Me.Core.Audio;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Voice;
using Xunit;
using DataAnchorLevel = Read2Me.Data.Enums.VoiceAnchorLevel;

namespace Read2Me.Tests.App.Characters
{
    public class VoiceRuleUiTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        // ── RuleDescription helper ─────────────────────────────────────────────

        [Fact]
        public void RuleDescription_Default_ShowsDefaultArrowVoice()
        {
            var row = new VoiceRuleRow(Guid.NewGuid(), IsDefault: true, Rank: "a0",
                VoiceId: Guid.NewGuid(), VoiceName: "Narrator",
                FromLevel: null, FromNodeId: null, FromDisplayName: null, FromDangling: false,
                ToLevel: null, ToNodeId: null, ToDisplayName: null, ToDangling: false);

            var desc = CharacterDetailPanel.RuleDescription(row);

            Assert.Equal("Default → Narrator", desc);
        }

        [Fact]
        public void RuleDescription_FromHereOnChapter_ShowsFromOnwardArrowVoice()
        {
            var row = new VoiceRuleRow(Guid.NewGuid(), IsDefault: false, Rank: "b0",
                VoiceId: Guid.NewGuid(), VoiceName: "Alice Voice",
                FromLevel: DataAnchorLevel.Chapter, FromNodeId: Guid.NewGuid(), FromDisplayName: "Chapter 5", FromDangling: false,
                ToLevel: null, ToNodeId: null, ToDisplayName: null, ToDangling: false);

            var desc = CharacterDetailPanel.RuleDescription(row);

            Assert.Equal("From Chapter Chapter 5 onward → Alice Voice", desc);
        }

        [Fact]
        public void RuleDescription_SingleNode_ShowsNodeArrowVoice()
        {
            var nodeId = Guid.NewGuid();
            var row = new VoiceRuleRow(Guid.NewGuid(), IsDefault: false, Rank: "b0",
                VoiceId: Guid.NewGuid(), VoiceName: "Bob Voice",
                FromLevel: DataAnchorLevel.Chapter, FromNodeId: nodeId, FromDisplayName: "Chapter 3", FromDangling: false,
                ToLevel: DataAnchorLevel.Chapter, ToNodeId: nodeId, ToDisplayName: "Chapter 3", ToDangling: false);

            var desc = CharacterDetailPanel.RuleDescription(row);

            Assert.Equal("Chapter Chapter 3 → Bob Voice", desc);
        }

        [Fact]
        public void RuleDescription_DanglingFrom_ShowsMissingNode()
        {
            var row = new VoiceRuleRow(Guid.NewGuid(), IsDefault: false, Rank: "b0",
                VoiceId: Guid.NewGuid(), VoiceName: "Voice",
                FromLevel: DataAnchorLevel.Chapter, FromNodeId: Guid.NewGuid(), FromDisplayName: null, FromDangling: true,
                ToLevel: null, ToNodeId: null, ToDisplayName: null, ToDangling: false);

            var desc = CharacterDetailPanel.RuleDescription(row);

            Assert.Contains("(missing node)", desc);
        }

        // ── Presenter VoiceRules property ─────────────────────────────────────

        [Fact]
        public async Task Presenter_SelectCharacter_LoadsVoiceRules()
        {
            var characterId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var ruleId = Guid.NewGuid();

            var expectedRules = new List<VoiceRuleRow>
            {
                new(ruleId, IsDefault: true, Rank: "a0",
                    VoiceId: voiceId, VoiceName: "Voice A",
                    null, null, null, false,
                    null, null, null, false)
            };

            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Folder)
                .Returns(new List<Character> { new() { Id = characterId, Name = "Alice" } });
            reader.GetCharacterLinesAsync(Folder, characterId).Returns(new List<CharacterLine>());
            reader.GetCharacterVoicesAsync(Folder, characterId).Returns(new List<Voice>());
            reader.GetDefaultVoiceIdAsync(Folder, characterId).Returns((Guid?)null);
            reader.GetCharacterVoiceRulesAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>()).Returns(expectedRules);

            var presenter = new CharacterPresenter(
                reader,
                Substitute.For<IBookCommandHandler>(),
                new VoiceOrchestrator(
                    Substitute.For<IAudioPipeline>(),
                    Substitute.For<ITranscriptionClientResolver>(),
                    Substitute.For<IVoiceAudioGenerator>(),
                    new FakeTranscriptionSettings(),
                    new FakeVoiceDesignPromptService(),
                    Substitute.For<IFileSystem>()));

            // LoadAsync sets _folderId; then SelectCharacterAsync uses it.
            await presenter.LoadAsync(Folder);
            await presenter.SelectCharacterAsync(new Character { Id = characterId, Name = "Alice" });

            Assert.Single(presenter.VoiceRules);
            Assert.Equal(ruleId, presenter.VoiceRules[0].Id);
        }

        // ── Presenter rule command methods ────────────────────────────────────

        [Fact]
        public async Task Presenter_CreateVoiceRule_ExecutesCommand()
        {
            var characterId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();

            var handler = Substitute.For<IBookCommandHandler>();
            handler.ExecuteAsync(Arg.Any<CreateVoiceRuleCommand>(), default)
                .Returns(ci => { return Task.FromResult<Guid?>(Guid.NewGuid()); });

            var reader = Substitute.For<IProjectReader>();
            reader.GetCharactersWithAliasesAsync(Arg.Any<ProjectFolderId>()).Returns(new List<Character>());
            reader.GetCharacterVoiceRulesAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<VoiceRuleRow>());
            reader.GetCharacterVoicesAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>()).Returns(new List<Voice>());
            reader.GetDefaultVoiceIdAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>()).Returns((Guid?)null);
            reader.GetCharacterLinesAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>()).Returns(new List<CharacterLine>());

            var presenter = new CharacterPresenter(
                reader, handler,
                new VoiceOrchestrator(
                    Substitute.For<IAudioPipeline>(),
                    Substitute.For<ITranscriptionClientResolver>(),
                    Substitute.For<IVoiceAudioGenerator>(),
                    new FakeTranscriptionSettings(),
                    new FakeVoiceDesignPromptService(),
                    Substitute.For<IFileSystem>()));

            // Simulate folderId being set.
            await presenter.LoadAsync(Folder);

            await presenter.CreateVoiceRuleAsync(characterId, voiceId, null, null, null, null);

            await handler.Received(1).ExecuteAsync(
                Arg.Is<CreateVoiceRuleCommand>(c => c.CharacterId == characterId && c.VoiceId == voiceId),
                Arg.Any<CancellationToken>());
        }

        // ── Fakes ─────────────────────────────────────────────────────────────

        private sealed class FakeTranscriptionSettings : TranscriptionSettingsService
        {
            public FakeTranscriptionSettings() : base(null!, null!) { }
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult<TranscriptionServiceConfig?>(null);
        }

        private sealed class FakeVoiceDesignPromptService : VoiceDesignPromptService
        {
            public FakeVoiceDesignPromptService() : base(null!, null!, null!, null!) { }
            public override Task<string> BuildRenderedPromptAsync(string a, string b, string c) => Task.FromResult("");
            public override Task<VoiceDesignPromptService.GenerateResult> GenerateWithPromptAsync(
                string prompt, System.Threading.CancellationToken ct = default) =>
                Task.FromResult(new VoiceDesignPromptService.GenerateResult(
                    VoiceDesignPromptService.GenerateStatus.Failed, null, null));
        }
    }
}
