using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class VoiceCommandTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;
        private readonly FileSystemService _fs;

        public VoiceCommandTests()
        {
            _fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(_fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _svc = new BookCommandHandler(session, _fs);
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

        [Fact]
        public async Task CreateVoice_FirstVoice_IsDefault()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var voiceId = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "Alice Voice"));

            var voice = await db.Voices.FirstAsync();
            Assert.True(voice.IsDefault);
            Assert.Equal("Alice Voice", voice.Name);
            Assert.Equal(charId, voice.CharacterId);
        }

        [Fact]
        public async Task CreateVoice_SecondVoice_IsNotDefault()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "Voice 1"));
            await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "Voice 2"));

            var voices = await db.Voices.OrderBy(v => v.CreatedUtc).ToListAsync();
            Assert.True(voices[0].IsDefault);
            Assert.False(voices[1].IsDefault);
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

        [Fact]
        public async Task SetVoiceDefault_SwitchesDefault_OtherBecomesNonDefault()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            await _svc.ExecuteAsync(new SetVoiceDefaultCommand(_folder, id2!.Value));

            var v1 = await db.Voices.FindAsync(id1!.Value);
            var v2 = await db.Voices.FindAsync(id2!.Value);
            Assert.False(v1!.IsDefault);
            Assert.True(v2!.IsDefault);
        }

        [Fact]
        public async Task DeleteVoice_NonDefault_LeavesDefaultIntact()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id2!.Value));

            var voices = await db.Voices.ToListAsync();
            Assert.Single(voices);
            Assert.True(voices[0].IsDefault);
            Assert.Equal(id1!.Value, voices[0].Id);
        }

        [Fact]
        public async Task DeleteVoice_Default_ReElectsFirstRemaining()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);

            var id1 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V1"));
            var id2 = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V2"));

            // Delete the default (V1)
            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id1!.Value));

            var voices = await db.Voices.ToListAsync();
            Assert.Single(voices);
            Assert.True(voices[0].IsDefault);
            Assert.Equal(id2!.Value, voices[0].Id);
        }

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

        [Fact]
        public async Task DeleteVoice_WithAudioFile_DeletesFile()
        {
            await using var db = await OpenDbAsync();
            var charId = await SeedCharacterAsync(db);
            var id = await _svc.ExecuteAsync(new CreateVoiceCommand(_folder, charId, "V"));

            // Create a real audio file on disk
            var charFolder = System.IO.Path.Combine(FolderPath, "voices", charId.ToString());
            System.IO.Directory.CreateDirectory(charFolder);
            var audioPath = System.IO.Path.Combine(charFolder, $"{id}-v.wav");
            await System.IO.File.WriteAllBytesAsync(audioPath, [0x52, 0x49, 0x46, 0x46]);
            var relPath = $"voices/{charId}/{id}-v.wav";
            await _svc.ExecuteAsync(new SetVoiceAudioCommand(_folder, id!.Value, relPath));

            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, id!.Value));

            Assert.False(System.IO.File.Exists(audioPath));
        }
    }
}
