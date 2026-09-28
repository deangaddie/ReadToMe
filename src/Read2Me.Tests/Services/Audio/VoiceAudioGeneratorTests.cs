using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoiceAudioGeneratorTests : ProjectDbTestBase
    {
        private readonly FakeFileSystem _fs = new();

        /// <summary>A design client whose take is a WAV of the given length.</summary>
        private class FakeVoiceDesignClient(int durationMs = 9_000) : IVoiceDesignClient
        {
            public string? SampleText { get; private set; }

            public Task<Stream> DesignVoiceAsync(VoiceDesignServiceConfig config, string prompt, string sampleText, string? settingsOverrideJson, CancellationToken ct = default)
            {
                SampleText = sampleText;
                return Task.FromResult<Stream>(new MemoryStream(TestWav.Tone(durationMs)));
            }
        }

        private class BusyVoiceDesignClient : IVoiceDesignClient
        {
            public Task<Stream> DesignVoiceAsync(VoiceDesignServiceConfig config, string prompt, string sampleText, string? settingsOverrideJson, CancellationToken ct = default) =>
                throw new TtsBusyException("http://localhost:8004", "breeze-design");
        }

        private class FakeClientResolver(IVoiceDesignClient client) : IVoiceDesignClientResolver
        {
            public IVoiceDesignClient Resolve(VoiceDesignServiceType type) => client;
        }

        /// <summary>
        /// Stands in for the write adapter: it records what the generator handed it and stores the
        /// take where the real one would. The hard Reference Limit belongs to the audio pipeline
        /// (<see cref="FileAudioPipelineTests"/>); this stand-in refuses the same way, so the
        /// generator's answer to a refusal can be observed. Whether the take reaches the Book — and
        /// what happens to the file when it does not — is <see cref="VoiceAudioWriterTests"/>.
        /// </summary>
        private sealed class RecordingVoiceAudioWriter(IFileSystem fs) : IVoiceAudioWriter
        {
            public AudioStoreRequest? Request { get; private set; }
            public string? Transcript { get; private set; }
            public string? DesignPrompt { get; private set; }
            public int Stored { get; private set; }

            public Task<string> RecordUploadedAsync(AudioStoreRequest request, CancellationToken ct = default) =>
                throw new NotSupportedException("The generator only ever records a generated take.");

            public Task<BookMutationOutcome> DeleteVoiceAsync(
                ProjectFolderId folder, Guid voiceId, CancellationToken ct = default) =>
                throw new NotSupportedException("The generator only ever records a generated take.");

            public Task<BookMutationOutcome> SetVoiceSourceAsync(
                ProjectFolderId folder, Guid voiceId, bool isGenerated, CancellationToken ct = default) =>
                throw new NotSupportedException("The generator only ever records a generated take.");

            public async Task<string> RecordGeneratedAsync(
                AudioStoreRequest request, string transcript, string designPrompt, CancellationToken ct = default)
            {
                Request = request;
                Transcript = transcript;
                DesignPrompt = designPrompt;

                using var take = new MemoryStream();
                await request.Source.CopyToAsync(take, ct);
                take.Position = 0;
                ReferenceLimit.EnsureWithinHardLimit(WavHeader.TryReadDurationMs(take)!.Value, take.Length);

                take.Position = 0;
                var path = $"voices/{request.CharacterId}/{request.VoiceId}-voice.wav";
                await fs.WriteFileAsync(fs.ProjectFilePath(request.FolderId, path), take);
                Stored++;
                return path;
            }
        }

        private async Task<VoiceDesignSettingsService> SettingsAsync(
            VoiceDesignServiceType type = VoiceDesignServiceType.Breeze)
        {
            var dbOptions = new DbContextOptionsBuilder<Read2MeDbContext>()
                .UseSqlite($"Data Source={Path.Combine(TempDir, "app.db")}")
                .Options;
            var dbFactory = new TestDbContextFactory<Read2MeDbContext>(dbOptions);
            await using (var db = dbFactory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.VoiceDesignServiceConfigs.Add(new VoiceDesignServiceConfig { Id = 1, Name = "Test", Type = type });
                db.Settings.Add(new AppSettings { ActiveVoiceDesignConfigId = 1 });
                await db.SaveChangesAsync();
            }

            return new VoiceDesignSettingsService(dbFactory, NullLogger<VoiceDesignSettingsService>.Instance);
        }

        private VoiceGenerationRequest Request() => new()
        {
            FolderId = new ProjectFolderId(FolderName),
            CharacterId = Guid.NewGuid(),
            CharacterName = "Alice",
            VoiceId = Guid.NewGuid(),
            VoiceName = "Alice Voice",
            DesignPrompt = "Calm voice",
        };

        private async Task<VoiceAudioGenerator> GeneratorAsync(IVoiceDesignClient client, RecordingVoiceAudioWriter writer,
            VoiceDesignServiceType type = VoiceDesignServiceType.Breeze) =>
            new(await SettingsAsync(type), new FakeClientResolver(client), writer, _fs);

        [Fact]
        public async Task GenerateAsync_SucceedsAndRecordsTheTake()
        {
            var voiceAudio = new RecordingVoiceAudioWriter(_fs);
            var client = new FakeVoiceDesignClient();
            var generator = await GeneratorAsync(client, voiceAudio, VoiceDesignServiceType.VoxCpm2);
            var request = Request();

            var result = await generator.GenerateAsync(request, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.AudioFileName);
            Assert.Equal(1, voiceAudio.Stored);
            Assert.Equal(request.VoiceId, voiceAudio.Request!.VoiceId);
            Assert.Equal("Calm voice", voiceAudio.DesignPrompt);
            // With no sample text set, the take speaks the built-in sentence, and that is its
            // transcript: what a cloning TTS is handed with it.
            Assert.Equal(PromptTemplates.VoiceDesignSampleSentence, client.SampleText);
            Assert.Equal(PromptTemplates.VoiceDesignSampleSentence, result.Transcript);
            Assert.Equal(result.Transcript, voiceAudio.Transcript);
        }

        [Fact]
        public async Task GenerateAsync_WithinTheSoftLimit_ReportsItsLength_AndNoWarning()
        {
            var generator = await GeneratorAsync(new FakeVoiceDesignClient(12_000), new RecordingVoiceAudioWriter(_fs));

            var result = await generator.GenerateAsync(Request(), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(12.0, result.ReferenceSeconds!.Value, precision: 1);
            Assert.Null(result.ReferenceWarning);
        }

        [Fact]
        public async Task GenerateAsync_OverTheSoftLimit_KeepsTheTake_AndWarns()
        {
            var voiceAudio = new RecordingVoiceAudioWriter(_fs);
            var generator = await GeneratorAsync(new FakeVoiceDesignClient(18_000), voiceAudio);

            var result = await generator.GenerateAsync(Request(), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, voiceAudio.Stored);
            Assert.Equal(18.0, result.ReferenceSeconds!.Value, precision: 1);
            Assert.Equal(
                "The reference is 18 s, over the 15 s soft limit. A shorter clip clones more reliably.",
                result.ReferenceWarning);
        }

        [Fact]
        public async Task GenerateAsync_OverTheHardLimit_Fails_StoresNothing_AndSaysToShortenTheSampleText()
        {
            var voiceAudio = new RecordingVoiceAudioWriter(_fs);
            var generator = await GeneratorAsync(new FakeVoiceDesignClient(34_500), voiceAudio);

            var result = await generator.GenerateAsync(Request(), CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.False(result.IsBusy);
            Assert.Equal(0, voiceAudio.Stored);
            Assert.Empty(_fs.GetAllPaths());
            Assert.Equal(
                "The generated voice is 34.5 s; a voice's reference must be 30 s or shorter. " +
                "Shorten the voice-design sample text and generate again.",
                result.ErrorMessage);
        }

        [Fact]
        public async Task GenerateAsync_WhenTheTtsIsBusy_ReportsBusyNotAGenericFailure()
        {
            var voiceAudio = new RecordingVoiceAudioWriter(_fs);
            var generator = await GeneratorAsync(new BusyVoiceDesignClient(), voiceAudio);

            var result = await generator.GenerateAsync(Request(), CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.True(result.IsBusy);
            Assert.Equal("TTS busy, try again", result.ErrorMessage);
            Assert.Equal(0, voiceAudio.Stored);
        }

        private class TestDbContextFactory<T>(DbContextOptions<T> options) : IDbContextFactory<T> where T : DbContext
        {
            public T CreateDbContext() => (T)Activator.CreateInstance(typeof(T), options)!;
        }
    }
}
