using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class VoiceCommandTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public VoiceCommandTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _svc = sp.GetRequiredService<BookCommandHandler>();
            _folder = new ProjectFolderId(FolderName);
        }

        private async Task<Guid> SeedCharacterAsync(ProjectDbContext db, string name = "Alice")
        {
            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "t.epub", Type = BookFileType.Epub });
            var c = new Character { Id = Guid.NewGuid(), Name = name };
            db.Characters.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        }

        // ── CreateVoice ───────────────────────────────────────────────────────

        [Fact]
        public async Task CreateVoice_FirstVoice_CreatesDefaultRule()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var voiceId = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "Alice Voice"));

            var rule = await db.VoiceRules.FirstOrDefaultAsync(r => r.CharacterId == charId);
            Assert.NotNull(rule);
            Assert.True(rule.IsDefault);
            Assert.Equal(voiceId, rule.VoiceId);
            Assert.Null(rule.FromLevel);
            Assert.Null(rule.FromNodeId);
            Assert.Null(rule.ToLevel);
            Assert.Null(rule.ToNodeId);
        }

        [Fact]
        public async Task CreateVoice_FirstVoice_DefaultRuleHasFloorRank()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));

            var rule = await db.VoiceRules.FirstAsync(r => r.CharacterId == charId && r.IsDefault);
            Assert.Equal("a0", rule.Rank);
        }

        [Fact]
        public async Task CreateVoice_SecondVoice_DoesNotCreateAnotherDefaultRule()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "Voice 1"));
            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "Voice 2"));

            var defaultRuleCount = await db.VoiceRules.CountAsync(r => r.CharacterId == charId && r.IsDefault);
            Assert.Equal(1, defaultRuleCount);
        }

        [Fact]
        public async Task CreateVoice_EmptyName_DefaultsToCharacterName()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db, "Bob");

            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, ""));

            var voice = await db.Voices.FirstAsync();
            Assert.Equal("Bob", voice.Name);
        }

        // ── SetVoiceDefault ───────────────────────────────────────────────────

        [Fact]
        public async Task SetVoiceDefault_RepontsDefaultRuleVoiceId()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            await _svc.ExecuteAsync(new SetVoiceDefaultCommand(_folder, id2!.Value));

            var rule = await db.VoiceRules.FirstAsync(r => r.CharacterId == charId && r.IsDefault);
            Assert.Equal(id2.Value, rule.VoiceId);
        }

        [Fact]
        public async Task SetVoiceDefault_DoesNotCreateNewRule()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            await _svc.ExecuteAsync(new SetVoiceDefaultCommand(_folder, id2!.Value));

            var ruleCount = await db.VoiceRules.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(1, ruleCount); // still exactly one default rule
        }

        // ── DeleteVoice ───────────────────────────────────────────────────────

        [Fact]
        public async Task DeleteVoice_NonDefault_DefaultRuleStillPointsToOriginalVoice()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id2!.Value));

            var rule = await db.VoiceRules.FirstAsync(r => r.CharacterId == charId && r.IsDefault);
            Assert.Equal(id1!.Value, rule.VoiceId);
        }

        [Fact]
        public async Task DeleteVoice_DefaultRuleTarget_RepontsToOldestRemaining()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            // id1 is the default rule target; delete it
            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id1!.Value));

            var rule = await db.VoiceRules.FirstAsync(r => r.CharacterId == charId && r.IsDefault);
            Assert.Equal(id2!.Value, rule.VoiceId);
        }

        [Fact]
        public async Task DeleteVoice_LastVoice_RemovesDefaultRule()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));
            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id!.Value));

            var ruleCount = await db.VoiceRules.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(0, ruleCount);
        }

        // ── Other voice commands ──────────────────────────────────────────────

        [Fact]
        public async Task SetVoiceAudio_StoresFileName()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));

            await _svc.ExecuteAsync(new SetVoiceAudioCommand(_folder, id!.Value, "voices/char/voice.wav"));

            var voice = await db.Voices.FindAsync(id!.Value);
            Assert.Equal("voices/char/voice.wav", voice!.AudioFileName);
        }

        [Fact]
        public async Task SetVoiceDesignPrompt_StoresPrompt()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));

            await _svc.ExecuteAsync(new SetVoiceDesignPromptCommand(_folder, id!.Value, "A gruff old man."));

            var voice = await db.Voices.FindAsync(id!.Value);
            Assert.Equal("A gruff old man.", voice!.DesignPrompt);
        }

        // ── SetVoiceTtsSettingsOverride ───────────────────────────────────────

        [Fact]
        public async Task SetVoiceTtsSettingsOverride_StoresTtsOverrideJson()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));

            await _svc.ExecuteAsync(new SetVoiceTtsSettingsOverrideCommand(_folder, id!.Value, "{\"cfg_value\":3.5}"));

            var voice = await db.Voices.FindAsync(id!.Value);
            Assert.Equal("{\"cfg_value\":3.5}", voice!.TtsSettingsOverrideJson);
        }

        [Fact]
        public async Task SetVoiceTtsSettingsOverride_Null_ClearsTtsOverrideJson()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));

            await _svc.ExecuteAsync(new SetVoiceTtsSettingsOverrideCommand(_folder, id!.Value, "{\"cfg_value\":3.5}"));
            await _svc.ExecuteAsync(new SetVoiceTtsSettingsOverrideCommand(_folder, id!.Value, null));

            var voice = await db.Voices.FindAsync(id!.Value);
            Assert.Null(voice!.TtsSettingsOverrideJson);
        }

        [Fact]
        public async Task DeleteVoice_WithAudioFile_DeletesFile()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));

            var audioPath = await SeedVoiceAudioAsync(charId, id!.Value);

            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id!.Value));

            Assert.False(System.IO.File.Exists(audioPath));
        }

        // ── The stored original ───────────────────────────────────────────────
        //
        // {voiceId}.orig.wav exists ⟺ the voice's audio has been edited. Both commands below drop the
        // live WAV, so an original that outlived it would claim an edit on audio that is gone — and
        // the Edited chip, Restore, and the regenerate confirm all read that one file.

        [Fact]
        public async Task DeleteVoice_DropsTheStoredOriginal()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));
            await SeedVoiceAudioAsync(charId, id!.Value);
            var originalPath = SeedStoredOriginal(charId, id!.Value);

            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id!.Value));

            Assert.False(System.IO.File.Exists(originalPath));
        }

        [Fact]
        public async Task SetVoiceSource_ToGenerated_DropsTheStoredOriginal()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));
            var audioPath = await SeedVoiceAudioAsync(charId, id!.Value);
            var originalPath = SeedStoredOriginal(charId, id!.Value);

            await _svc.ExecuteAsync(new SetVoiceSourceCommand(_folder, id!.Value, IsGenerated: true));

            Assert.False(System.IO.File.Exists(audioPath));
            Assert.False(System.IO.File.Exists(originalPath));
        }

        /// <summary>Writes a live WAV for the voice and points the row at it. Returns its full path.</summary>
        private async Task<string> SeedVoiceAudioAsync(Guid charId, Guid voiceId)
        {
            var charFolder = System.IO.Path.Combine(FolderPath, "voices", charId.ToString());
            System.IO.Directory.CreateDirectory(charFolder);
            var audioPath = System.IO.Path.Combine(charFolder, $"{voiceId}-v.wav");
            await System.IO.File.WriteAllBytesAsync(audioPath, [0x52, 0x49, 0x46, 0x46]);

            await _svc.ExecuteAsync(
                new SetVoiceAudioCommand(_folder, voiceId, $"voices/{charId}/{voiceId}-v.wav"));

            return audioPath;
        }

        /// <summary>Writes the {voiceId}.orig.wav the editor leaves behind. Returns its full path.</summary>
        private string SeedStoredOriginal(Guid charId, Guid voiceId)
        {
            var path = System.IO.Path.Combine(
                FolderPath, "voices", charId.ToString(), $"{voiceId}.orig.wav");
            System.IO.File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);
            return path;
        }
    }
}
