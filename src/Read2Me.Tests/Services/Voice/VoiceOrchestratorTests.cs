using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Read2Me.App.Services;
using Read2Me.AppData.Entities;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Voice;
using Xunit;

namespace Read2Me.Tests.Services.Voice
{
    public class VoiceOrchestratorTests
    {
        private sealed class FakeTranscriptionSettings : TranscriptionSettingsService
        {
            private readonly TranscriptionServiceConfig? _config;
            public FakeTranscriptionSettings(TranscriptionServiceConfig? config) : base(null!, null!) => _config = config;
            public override Task<TranscriptionServiceConfig?> GetActiveConfigAsync() => Task.FromResult(_config);
        }

        private sealed class FakeVoiceDesignPromptService : VoiceDesignPromptService
        {
            private readonly GenerateResult _result;
            public FakeVoiceDesignPromptService(GenerateResult result) : base(null!, null!, null!, null!) => _result = result;
            public override Task<GenerateResult> GenerateWithPromptAsync(string renderedPrompt, CancellationToken ct = default)
                => Task.FromResult(_result);
        }

        private static VoiceOrchestrator Create(
            IAudioPipeline? audioPipeline = null,
            ITranscriptionClientResolver? resolver = null,
            IVoiceAudioGenerator? voiceAudioGenerator = null,
            TranscriptionSettingsService? transcriptionSettings = null,
            VoiceDesignPromptService? voiceDesignPromptService = null,
            IFileSystem? fileSystem = null)
        {
            return new VoiceOrchestrator(
                audioPipeline: audioPipeline ?? Substitute.For<IAudioPipeline>(),
                transcriptionResolver: resolver ?? Substitute.For<ITranscriptionClientResolver>(),
                voiceAudioGenerator: voiceAudioGenerator ?? Substitute.For<IVoiceAudioGenerator>(),
                transcriptionSettings: transcriptionSettings ?? new FakeTranscriptionSettings(null),
                voiceDesignPromptService: voiceDesignPromptService ?? new FakeVoiceDesignPromptService(
                    new VoiceDesignPromptService.GenerateResult(VoiceDesignPromptService.GenerateStatus.Failed, null, null)),
                fileSystem: fileSystem ?? Substitute.For<IFileSystem>());
        }

        // ── Upload ────────────────────────────────────────────────────────────

        [Fact]
        public async Task StoreAudioAsync_PipelineCalledWithRequest_ReturnsFilename()
        {
            var pipeline = Substitute.For<IAudioPipeline>();
            var request = new AudioStoreRequest
            {
                FolderId = new ProjectFolderId("f"),
                CharacterId = Guid.NewGuid(),
                VoiceId = Guid.NewGuid(),
                VoiceName = "Alice",
                Source = new MemoryStream(new byte[] { 1, 2, 3 }),
                Extension = ".wav",
            };
            pipeline.StoreAsync(request, Arg.Any<CancellationToken>()).Returns("voices/alice.wav");

            var sut = Create(audioPipeline: pipeline);
            var result = await sut.StoreAudioAsync(request);

            Assert.Equal("voices/alice.wav", result);
            await pipeline.Received(1).StoreAsync(request, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task StoreAudioAsync_PipelineThrows_PropagatesException()
        {
            var pipeline = Substitute.For<IAudioPipeline>();
            pipeline.StoreAsync(Arg.Any<AudioStoreRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new IOException("disk full"));

            var sut = Create(audioPipeline: pipeline);

            await Assert.ThrowsAsync<IOException>(() =>
                sut.StoreAudioAsync(new AudioStoreRequest
                {
                    FolderId = new ProjectFolderId("f"),
                    CharacterId = Guid.NewGuid(),
                    VoiceId = Guid.NewGuid(),
                    VoiceName = "V",
                    Source = new MemoryStream(),
                    Extension = ".wav",
                }));
        }

        // ── Transcribe ────────────────────────────────────────────────────────

        [Fact]
        public async Task TranscribeAsync_NoActiveConfig_Throws()
        {
            var sut = Create(transcriptionSettings: new FakeTranscriptionSettings(null));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.TranscribeAsync(new ProjectFolderId("f"), Guid.NewGuid(), new MemoryStream(), "audio.wav"));

            Assert.Equal("No active transcription server configured.", ex.Message);
        }

        [Fact]
        public async Task TranscribeAsync_ClientThrows_PropagatesException()
        {
            var config = new TranscriptionServiceConfig { Type = TranscriptionServiceType.LocalWhisper };
            var client = Substitute.For<ITranscriptionClient>();
            client.TranscribeAsync(Arg.Any<TranscriptionServiceConfig>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("whisper unavailable"));

            var resolver = Substitute.For<ITranscriptionClientResolver>();
            resolver.Resolve(TranscriptionServiceType.LocalWhisper).Returns(client);

            var sut = Create(
                resolver: resolver,
                transcriptionSettings: new FakeTranscriptionSettings(config));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.TranscribeAsync(new ProjectFolderId("f"), Guid.NewGuid(), new MemoryStream(), "audio.wav"));
        }

        [Fact]
        public async Task TranscribeAsync_Success_ReturnsTranscript()
        {
            var config = new TranscriptionServiceConfig { Type = TranscriptionServiceType.LocalWhisper };
            var client = Substitute.For<ITranscriptionClient>();
            client.TranscribeAsync(Arg.Any<TranscriptionServiceConfig>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("Hello world.");

            var resolver = Substitute.For<ITranscriptionClientResolver>();
            resolver.Resolve(TranscriptionServiceType.LocalWhisper).Returns(client);

            var sut = Create(
                resolver: resolver,
                transcriptionSettings: new FakeTranscriptionSettings(config));

            var result = await sut.TranscribeAsync(new ProjectFolderId("f"), Guid.NewGuid(), new MemoryStream(), "audio.wav");

            Assert.Equal("Hello world.", result);
        }

        // ── GenerateVoiceAudio ────────────────────────────────────────────────

        [Fact]
        public async Task GenerateVoiceAudioAsync_GeneratorReturnsSuccess_ReturnsResult()
        {
            var generator = Substitute.For<IVoiceAudioGenerator>();
            var request = new VoiceGenerationRequest
            {
                FolderId = new ProjectFolderId("f"),
                CharacterId = Guid.NewGuid(),
                VoiceId = Guid.NewGuid(),
                VoiceName = "Alice",
                DesignPrompt = "warm, deep",
            };
            var expected = VoiceGenerationResult.Success("voices/alice.wav", "Hello world.");
            generator.GenerateAsync(request, Arg.Any<CancellationToken>()).Returns(expected);

            var sut = Create(voiceAudioGenerator: generator);
            var result = await sut.GenerateVoiceAudioAsync(request);

            Assert.True(result.IsSuccess);
            Assert.Equal("voices/alice.wav", result.AudioFileName);
            Assert.Equal("Hello world.", result.Transcript);
        }

        [Fact]
        public async Task GenerateVoiceAudioAsync_GeneratorReturnsFailure_ReturnsFailureResult()
        {
            var generator = Substitute.For<IVoiceAudioGenerator>();
            var request = new VoiceGenerationRequest
            {
                FolderId = new ProjectFolderId("f"),
                CharacterId = Guid.NewGuid(),
                VoiceId = Guid.NewGuid(),
                VoiceName = "Alice",
                DesignPrompt = "warm, deep",
            };
            var expected = VoiceGenerationResult.Failure("TTS server offline");
            generator.GenerateAsync(request, Arg.Any<CancellationToken>()).Returns(expected);

            var sut = Create(voiceAudioGenerator: generator);
            var result = await sut.GenerateVoiceAudioAsync(request);

            Assert.False(result.IsSuccess);
            Assert.Equal("TTS server offline", result.ErrorMessage);
        }

        // ── GenerateWithPrompt ────────────────────────────────────────────────

        [Fact]
        public async Task GenerateWithPromptAsync_LlmFailure_ThrowsWithFailureReason()
        {
            var sut = Create(voiceDesignPromptService: new FakeVoiceDesignPromptService(
                new VoiceDesignPromptService.GenerateResult(VoiceDesignPromptService.GenerateStatus.Failed, null, "LLM timed out")));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.GenerateWithPromptAsync("some prompt"));

            Assert.Equal("LLM timed out", ex.Message);
        }

        [Fact]
        public async Task GenerateWithPromptAsync_LlmFailureNullReason_ThrowsDefaultMessage()
        {
            var sut = Create(voiceDesignPromptService: new FakeVoiceDesignPromptService(
                new VoiceDesignPromptService.GenerateResult(VoiceDesignPromptService.GenerateStatus.Failed, null, null)));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.GenerateWithPromptAsync("some prompt"));

            Assert.Equal("Failed to generate voice prompt.", ex.Message);
        }

        [Fact]
        public async Task GenerateWithPromptAsync_Success_ReturnsPromptString()
        {
            var sut = Create(voiceDesignPromptService: new FakeVoiceDesignPromptService(
                new VoiceDesignPromptService.GenerateResult(VoiceDesignPromptService.GenerateStatus.Success, "deep baritone, warm", null)));

            var result = await sut.GenerateWithPromptAsync("describe Alice");

            Assert.Equal("deep baritone, warm", result);
        }

        // ── OpenAudioStream ───────────────────────────────────────────────────

        [Fact]
        public void OpenAudioStream_FileExists_ReturnsStream()
        {
            var tmpPath = Path.GetTempFileName();
            try
            {
                var fs = Substitute.For<IFileSystem>();
                fs.GetProjectFolderPath("f").Returns(Path.GetDirectoryName(tmpPath)!);
                fs.FileExists(Arg.Any<string>()).Returns(true);

                var sut = Create(fileSystem: fs);
                using var stream = sut.OpenAudioStream(new ProjectFolderId("f"), Path.GetFileName(tmpPath));

                Assert.NotNull(stream);
            }
            finally
            {
                File.Delete(tmpPath);
            }
        }

        [Fact]
        public void OpenAudioStream_FileMissing_ReturnsNull()
        {
            var fs = Substitute.For<IFileSystem>();
            fs.GetProjectFolderPath("f").Returns(@"C:\data\f");
            fs.FileExists(Arg.Any<string>()).Returns(false);

            var sut = Create(fileSystem: fs);
            var result = sut.OpenAudioStream(new ProjectFolderId("f"), "voices/missing.wav");

            Assert.Null(result);
        }

        [Fact]
        public void OpenAudioStream_NullFileName_ReturnsNull()
        {
            var sut = Create();
            var result = sut.OpenAudioStream(new ProjectFolderId("f"), null);

            Assert.Null(result);
        }
    }
}
