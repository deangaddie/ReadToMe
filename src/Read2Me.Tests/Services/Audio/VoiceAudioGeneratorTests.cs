using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoiceAudioGeneratorTests : ProjectDbTestBase
    {
        private class FakeVoiceDesignClient : IVoiceDesignClient
        {
            public Task<Stream> DesignVoiceAsync(VoiceDesignServiceConfig config, string prompt, string sampleText, string? settingsOverrideJson, CancellationToken ct = default)
            {
                return Task.FromResult<Stream>(new MemoryStream([0x52, 0x49, 0x46, 0x46]));
            }
        }

        private class FakeClientResolver(IVoiceDesignClient client) : IVoiceDesignClientResolver
        {
            public IVoiceDesignClient Resolve(VoiceDesignServiceType type) => client;
        }

        /// <summary>
        /// Stands in for the write adapter: it records what the generator handed it, which is the
        /// only thing this test is about. Whether that reaches the Book — and what happens to the
        /// file when it does not — is <see cref="VoiceAudioWriterTests"/>.
        /// </summary>
        private sealed class RecordingVoiceAudioWriter : IVoiceAudioWriter
        {
            public AudioStoreRequest? Request { get; private set; }
            public string? Transcript { get; private set; }
            public string? DesignPrompt { get; private set; }
            public int Calls { get; private set; }

            public Task<string> RecordUploadedAsync(AudioStoreRequest request, CancellationToken ct = default) =>
                throw new NotSupportedException("The generator only ever records a generated take.");

            public Task<BookMutationOutcome> DeleteVoiceAsync(
                ProjectFolderId folder, Guid voiceId, CancellationToken ct = default) =>
                throw new NotSupportedException("The generator only ever records a generated take.");

            public Task<BookMutationOutcome> SetVoiceSourceAsync(
                ProjectFolderId folder, Guid voiceId, bool isGenerated, CancellationToken ct = default) =>
                throw new NotSupportedException("The generator only ever records a generated take.");

            public Task<string> RecordGeneratedAsync(
                AudioStoreRequest request, string transcript, string designPrompt, CancellationToken ct = default)
            {
                Calls++;
                Request = request;
                Transcript = transcript;
                DesignPrompt = designPrompt;
                return Task.FromResult($"voices/{request.CharacterId}/{request.VoiceId}-voice.wav");
            }
        }

        [Fact]
        public async Task GenerateAsync_SucceedsAndRecordsTheTake()
        {
            // Setup
            var dbOptions = new DbContextOptionsBuilder<Read2MeDbContext>()
                .UseSqlite($"Data Source={Path.Combine(TempDir, "app.db")}")
                .Options;
            var dbFactory = new TestDbContextFactory<Read2MeDbContext>(dbOptions);
            await using (var db = dbFactory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.VoiceDesignServiceConfigs.Add(new VoiceDesignServiceConfig { Id = 1, Name = "Test", Type = VoiceDesignServiceType.VoxCpm2 });
                db.Settings.Add(new AppSettings { ActiveVoiceDesignConfigId = 1 });
                await db.SaveChangesAsync();
            }

            var settings = new VoiceDesignSettingsService(dbFactory, NullLogger<VoiceDesignSettingsService>.Instance);
            var client = new FakeVoiceDesignClient();
            var resolver = new FakeClientResolver(client);
            var voiceAudio = new RecordingVoiceAudioWriter();

            var generator = new VoiceAudioGenerator(settings, resolver, voiceAudio);

            var request = new VoiceGenerationRequest
            {
                FolderId = new ProjectFolderId(FolderName),
                CharacterId = Guid.NewGuid(),
                CharacterName = "Alice",
                VoiceId = Guid.NewGuid(),
                VoiceName = "Alice Voice",
                DesignPrompt = "Calm voice"
            };

            // Act
            var result = await generator.GenerateAsync(request, CancellationToken.None);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.AudioFileName);
            Assert.Equal(1, voiceAudio.Calls);
            Assert.Equal(request.VoiceId, voiceAudio.Request!.VoiceId);
            Assert.Equal("Calm voice", voiceAudio.DesignPrompt);
            // The take speaks the sample sentence, and that is what a cloning TTS is handed with it.
            Assert.Equal(result.Transcript, voiceAudio.Transcript);
        }

        private class TestDbContextFactory<T>(DbContextOptions<T> options) : IDbContextFactory<T> where T : DbContext
        {
            public T CreateDbContext() => (T)Activator.CreateInstance(typeof(T), options)!;
        }

    }
}
