using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class FileAudioPipelineTests
    {
        private readonly FakeFileSystem _fs;
        private readonly FakeNormalizerForPipeline _normalizer;
        private readonly FakeAudioProcessingSettingsForPipeline _settings;
        private readonly FileAudioPipeline _pipeline;
        private readonly ProjectFolderId _folder;

        public FileAudioPipelineTests()
        {
            _fs = new FakeFileSystem();
            _normalizer = new FakeNormalizerForPipeline();
            _settings = new FakeAudioProcessingSettingsForPipeline("ffmpeg");
            _pipeline = new FileAudioPipeline(_fs, _normalizer, _settings);
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

        // ── Issue 003: normalisation in StoreAsync ────────────────────────────────

        [Theory]
        [InlineData(".mp3")]
        [InlineData(".flac")]
        [InlineData(".aac")]
        [InlineData(".wav")]
        public async Task StoreAsync_AlwaysStoresWav_RegardlessOfInputExtension(string ext)
        {
            var req = MakeRequest(ext);
            var result = await _pipeline.StoreAsync(req);
            Assert.EndsWith(".wav", result);
        }

        [Fact]
        public async Task StoreAsync_WritesNormalisedBytes_NotRawInput()
        {
            var inputBytes = new byte[] { 0x01, 0x02, 0x03 };
            var normalisedBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            _normalizer.ReturnBytes = normalisedBytes;

            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var req = new AudioStoreRequest
            {
                FolderId = _folder,
                CharacterId = charId,
                CharacterName = "Bob",
                CharacterAliases = [],
                VoiceId = voiceId,
                VoiceName = "Bob Voice",
                Source = new MemoryStream(inputBytes),
                Extension = ".mp3"
            };

            await _pipeline.StoreAsync(req);

            var filePath = System.IO.Path.Combine("C:\\fake-workspace", "TestProject", "voices",
                charId.ToString(), $"{voiceId}-bob-voice.wav");
            Assert.Equal(normalisedBytes, _fs.GetFileContent(filePath));
        }

        [Fact]
        public async Task StoreAsync_InvokesNormaliser_WithRequestSourceStream()
        {
            var req = MakeRequest(".mp3");
            await _pipeline.StoreAsync(req);
            Assert.True(_normalizer.WasCalled);
        }

        [Theory]
        [InlineData(".mp3")]
        [InlineData(".ogg")]
        [InlineData(".flac")]
        public async Task StoreAsync_PreviouslyDisallowedExtensions_NoLongerThrow(string ext)
        {
            var req = MakeRequest(ext);
            // Should not throw — any exception means the test fails
            var result = await _pipeline.StoreAsync(req);
            Assert.False(string.IsNullOrEmpty(result));
        }

        // ── StoreParagraphAudioAsync unchanged ────────────────────────────────────

        [Fact]
        public async Task StoreParagraphAudioAsync_DoesNotInvokeNormaliser()
        {
            await _pipeline.StoreParagraphAudioAsync(_folder, Guid.NewGuid(), AudioStream());
            Assert.False(_normalizer.WasCalled);
        }

        [Fact]
        public async Task StoreParagraphAudioAsync_ReturnsCorrectRelativePath()
        {
            var itemId = Guid.NewGuid();
            var result = await _pipeline.StoreParagraphAudioAsync(_folder, itemId, AudioStream());
            Assert.Equal($"audio/{itemId}.wav", result);
        }

        [Fact]
        public async Task StoreParagraphAudioAsync_UsesForwardSlashes()
        {
            var result = await _pipeline.StoreParagraphAudioAsync(_folder, Guid.NewGuid(), AudioStream());
            Assert.DoesNotContain('\\', result);
        }

        [Fact]
        public async Task StoreParagraphAudioAsync_WritesFileToAudioSubfolder()
        {
            var itemId = Guid.NewGuid();
            await _pipeline.StoreParagraphAudioAsync(_folder, itemId, AudioStream());
            var expected = System.IO.Path.Combine("C:\\fake-workspace", "TestProject", "audio", $"{itemId}.wav");
            Assert.True(_fs.FileExists(expected));
        }

        [Fact]
        public async Task StoreParagraphAudioAsync_WritesCorrectContent()
        {
            var itemId = Guid.NewGuid();
            var data = new byte[] { 0xAA, 0xBB, 0xCC };
            await _pipeline.StoreParagraphAudioAsync(_folder, itemId, new MemoryStream(data));
            var expected = System.IO.Path.Combine("C:\\fake-workspace", "TestProject", "audio", $"{itemId}.wav");
            Assert.Equal(data, _fs.GetFileContent(expected));
        }

        [Fact]
        public async Task StoreParagraphAudioAsync_NoCharacterSubfolderOrHelperFile()
        {
            await _pipeline.StoreParagraphAudioAsync(_folder, Guid.NewGuid(), AudioStream());
            var files = _fs.GetAllPaths();
            Assert.DoesNotContain(files, p => p.EndsWith(".txt"));
            Assert.DoesNotContain(files, p => p.Contains("voices"));
        }

        // ── Existing behaviour preserved ──────────────────────────────────────────

        [Fact]
        public async Task StoreAsync_ReturnsCorrectRelativePath()
        {
            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var req = MakeRequest(".wav", voiceName: "My Voice", charId: charId, voiceId: voiceId);

            var result = await _pipeline.StoreAsync(req);

            Assert.Equal($"voices/{charId}/{voiceId}-my-voice.wav", result);
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

            var txtPath = System.IO.Path.Combine("C:\\fake-workspace", "TestProject", "voices", charId.ToString(), "alice.txt");
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

            var txtPath = System.IO.Path.Combine("C:\\fake-workspace", "TestProject", "voices", charId.ToString(), "alice.txt");
            var content = System.Text.Encoding.UTF8.GetString(_fs.GetFileContent(txtPath));
            Assert.Contains("Alice", content);
            Assert.DoesNotContain("Alice CHANGED", content);
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

        // ── Fake helpers ──────────────────────────────────────────────────────────

        private sealed class FakeNormalizerForPipeline : IAudioNormalizer
        {
            public bool WasCalled { get; private set; }
            public byte[]? ReturnBytes { get; set; }

            public Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default) =>
                throw new NotSupportedException();

            public async Task<Stream> NormalizeToWavAsync(Stream input, string? ffmpegPath, CancellationToken ct = default)
            {
                WasCalled = true;
                if (ReturnBytes is not null)
                    return new MemoryStream(ReturnBytes);

                var ms = new MemoryStream();
                if (input.CanSeek) input.Position = 0;
                await input.CopyToAsync(ms, ct);
                ms.Position = 0;
                return ms;
            }
        }

        private sealed class FakeAudioProcessingSettingsForPipeline : AudioProcessingSettingsService
        {
            private readonly string? _ffmpegPath;

            public FakeAudioProcessingSettingsForPipeline(string? ffmpegPath)
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance)
            {
                _ffmpegPath = ffmpegPath;
            }

            public override Task<AudioProcessingSettings> GetAsync() =>
                Task.FromResult(new AudioProcessingSettings(
                    _ffmpegPath, WerThreshold: 0.15,
                    SentenceSplitEnabled: false, ChunkPauseMs: 300,
                    VolumePauseMs: 4000, PartPauseMs: 3000, ChapterPauseMs: 2500,
                    ParagraphPauseMs: 800, PauseMs: 500));
        }
    }
}
