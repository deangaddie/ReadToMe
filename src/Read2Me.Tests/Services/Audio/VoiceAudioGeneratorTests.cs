using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Services;
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

        private class FakeAudioPipeline : IAudioPipeline
        {
            public Task<string> StoreAsync(AudioStoreRequest request, CancellationToken ct = default)
            {
                return Task.FromResult($"voices/{request.CharacterId}/{request.VoiceId}-voice.wav");
            }
        }

        [Fact]
        public async Task GenerateAsync_SucceedsAndCallsCommandHandler()
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
            var pipeline = new FakeAudioPipeline();
            var commandHandler = new FakeCommandHandler();

            var generator = new VoiceAudioGenerator(settings, resolver, pipeline, commandHandler);

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
            Assert.Single(commandHandler.ExecutedCommands);
            var cmd = Assert.IsType<SetVoiceGeneratedCommand>(commandHandler.ExecutedCommands[0]);
            Assert.Equal(request.VoiceId, cmd.VoiceId);
            Assert.Equal(result.AudioFileName, cmd.AudioFileName);
            Assert.Equal("Calm voice", cmd.DesignPrompt);
        }

        private class TestDbContextFactory<T>(DbContextOptions<T> options) : IDbContextFactory<T> where T : DbContext
        {
            public T CreateDbContext() => (T)Activator.CreateInstance(typeof(T), options)!;
        }

        private class FakeCommandHandler : IBookCommandHandler
        {
            public System.Collections.Generic.List<BookCommand> ExecutedCommands { get; } = [];
            public Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
            {
                ExecutedCommands.Add(command);
                return Task.FromResult<Guid?>(null);
            }
        }
    }
}
