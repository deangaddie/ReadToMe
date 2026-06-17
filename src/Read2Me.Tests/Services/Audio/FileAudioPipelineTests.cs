using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Read2Me.Core.Audio;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class FileAudioPipelineTests : ProjectDbTestBase
    {
        private readonly FileAudioPipeline _pipeline;
        private readonly ProjectFolderId _folder;

        public FileAudioPipelineTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            _pipeline = new FileAudioPipeline(fs);
            _folder = new ProjectFolderId(FolderName);
        }

        private static Stream AudioStream() => new MemoryStream([0x52, 0x49, 0x46, 0x46]);

        private AudioStoreRequest MakeRequest(string ext, string voiceName = "My Voice", string charName = "Alice",
            string[]? aliases = null, Guid? voiceId = null, Guid? charId = null) => new()
        {
            FolderId = _folder,
            CharacterId = charId ?? Guid.NewGuid(),
            CharacterName = charName,
            CharacterAliases = aliases ?? [],
            VoiceId = voiceId ?? Guid.NewGuid(),
            VoiceName = voiceName,
            Source = AudioStream(),
            Extension = ext
        };

        [Theory]
        [InlineData(".wav")]
        [InlineData(".aac")]
        public async Task StoreAsync_AcceptedFormats_Succeeds(string ext)
        {
            var req = MakeRequest(ext);
            var result = await _pipeline.StoreAsync(req);
            Assert.False(string.IsNullOrEmpty(result));
        }

        [Theory]
        [InlineData(".mp3")]
        [InlineData(".ogg")]
        [InlineData(".flac")]
        public async Task StoreAsync_UnsupportedFormat_Throws(string ext)
        {
            var req = MakeRequest(ext);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _pipeline.StoreAsync(req));
        }

        [Fact]
        public async Task StoreAsync_ReturnsCorrectRelativePath()
        {
            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var req = MakeRequest(".wav", voiceName: "My Voice", charId: charId, voiceId: voiceId);

            var result = await _pipeline.StoreAsync(req);

            Assert.Equal($"voices/{charId}/{ voiceId}-my-voice.wav", result);
        }

        [Fact]
        public async Task StoreAsync_FilenameFormat_VoiceIdDashSanitizedNameDotExt()
        {
            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var req = MakeRequest(".aac", voiceName: "Alice Bright", charId: charId, voiceId: voiceId);

            await _pipeline.StoreAsync(req);

            var expectedFile = Path.Combine(TempDir, FolderName, "voices", charId.ToString(),
                $"{voiceId}-alice-bright.aac");
            Assert.True(File.Exists(expectedFile));
        }

        [Fact]
        public async Task StoreAsync_UsesForwardSlashesInPath()
        {
            var req = MakeRequest(".wav");
            var result = await _pipeline.StoreAsync(req);
            Assert.DoesNotContain('\\', result);
        }

        [Fact]
        public async Task StoreAsync_WritesHelperTextFile_WithCharacterNameAndAliases()
        {
            var charId = Guid.NewGuid();
            var req = MakeRequest(".wav", charName: "Alice", aliases: ["Al", "Ally"], charId: charId);

            await _pipeline.StoreAsync(req);

            var txtPath = Path.Combine(TempDir, FolderName, "voices", charId.ToString(), "alice.txt");
            Assert.True(File.Exists(txtPath));
            var lines = await File.ReadAllLinesAsync(txtPath);
            Assert.Equal("Alice", lines[0]);
            Assert.Contains("Al", lines);
            Assert.Contains("Ally", lines);
        }

        [Fact]
        public async Task StoreAsync_HelperTextFile_NotOverwrittenOnSecondVoice()
        {
            var charId = Guid.NewGuid();
            var req1 = MakeRequest(".wav", voiceName: "Voice One", charName: "Alice", aliases: ["Al"], charId: charId);
            var req2 = MakeRequest(".wav", voiceName: "Voice Two", charName: "Alice CHANGED", aliases: ["AlChanged"], charId: charId);

            await _pipeline.StoreAsync(req1);
            await _pipeline.StoreAsync(req2);

            var txtPath = Path.Combine(TempDir, FolderName, "voices", charId.ToString(), "alice.txt");
            var lines = await File.ReadAllLinesAsync(txtPath);
            Assert.Equal("Alice", lines[0]);
            Assert.DoesNotContain("Alice CHANGED", lines);
        }

        [Fact]
        public async Task StoreAsync_AudioContent_WrittenCorrectly()
        {
            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            var req = new AudioStoreRequest
            {
                FolderId = _folder,
                CharacterId = charId,
                CharacterName = "Bob",
                CharacterAliases = [],
                VoiceId = voiceId,
                VoiceName = "Bob Voice",
                Source = new MemoryStream(data),
                Extension = ".wav"
            };

            await _pipeline.StoreAsync(req);

            var filePath = Path.Combine(TempDir, FolderName, "voices", charId.ToString(), $"{voiceId}-bob-voice.wav");
            var written = await File.ReadAllBytesAsync(filePath);
            Assert.Equal(data, written);
        }
    }
}
