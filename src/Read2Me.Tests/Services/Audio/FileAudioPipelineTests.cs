using System;
using System.IO;
using System.Threading.Tasks;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class FileAudioPipelineTests
    {
        private readonly FileAudioPipeline _pipeline;
        private readonly FakeFileSystem _fs;
        private readonly ProjectFolderId _folder;

        public FileAudioPipelineTests()
        {
            _fs = new FakeFileSystem();
            _pipeline = new FileAudioPipeline(_fs);
            _folder = new ProjectFolderId("TestProject");
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

            var expectedFile = Path.Combine("C:\\fake-workspace", "TestProject", "voices", charId.ToString(),
                $"{voiceId}-alice-bright.aac");
            Assert.True(_fs.FileExists(expectedFile));
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

            var txtPath = Path.Combine("C:\\fake-workspace", "TestProject", "voices", charId.ToString(), "alice.txt");
            Assert.True(_fs.FileExists(txtPath));
            var content = System.Text.Encoding.UTF8.GetString(_fs.GetFileContent(txtPath));
            Assert.Contains("Alice", content);
            Assert.Contains("Al", content);
            Assert.Contains("Ally", content);
        }

        [Fact]
        public async Task StoreAsync_HelperTextFile_NotOverwrittenOnSecondVoice()
        {
            var charId = Guid.NewGuid();
            var req1 = MakeRequest(".wav", voiceName: "Voice One", charName: "Alice", aliases: ["Al"], charId: charId);
            var req2 = MakeRequest(".wav", voiceName: "Voice Two", charName: "Alice CHANGED", aliases: ["AlChanged"], charId: charId);

            await _pipeline.StoreAsync(req1);
            await _pipeline.StoreAsync(req2);

            var txtPath = Path.Combine("C:\\fake-workspace", "TestProject", "voices", charId.ToString(), "alice.txt");
            var content = System.Text.Encoding.UTF8.GetString(_fs.GetFileContent(txtPath));
            Assert.Contains("Alice", content);
            Assert.DoesNotContain("Alice CHANGED", content);
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

            var filePath = Path.Combine("C:\\fake-workspace", "TestProject", "voices", charId.ToString(), $"{voiceId}-bob-voice.wav");
            var written = _fs.GetFileContent(filePath);
            Assert.Equal(data, written);
        }

        [Fact]
        public async Task StoreAsync_FallsBackToVoiceIdPrefix_WhenSanitizedNameEmpty()
        {
            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var req = MakeRequest(".wav", voiceName: "!!!", charId: charId, voiceId: voiceId);

            var result = await _pipeline.StoreAsync(req);

            var expectedPrefix = voiceId.ToString("N")[..8];
            Assert.Contains(expectedPrefix, result);
            Assert.EndsWith(".wav", result);
        }
    }
}
