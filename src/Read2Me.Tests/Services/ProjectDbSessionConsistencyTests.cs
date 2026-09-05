using System.Reflection;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services
{
    public class ProjectDbSessionConsistencyTests : IAsyncDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectDbSession _session;
        private readonly IFileSystem _fs;
        private readonly ProjectFolderId _folder;

        public ProjectDbSessionConsistencyTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "SessionConsistency_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = _tempDir }));
            _session = new ProjectDbSession(_fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _folder = new ProjectFolderId("test-session");
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null) await _services.DisposeAsync();
            await _session.DisposeAsync();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private async Task SeedProjectDbAsync()
        {
            var folderPath = Path.Combine(_tempDir, _folder.Value);
            Directory.CreateDirectory(folderPath);
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(folderPath, "project.db")};Pooling=false")
                .Options;
            await using var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            db.Projects.Add(new Project { Title = "Test", BookTitle = "T", Author = "A", Filename = "f.epub", Type = BookFileType.Epub });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Import_ThenRead_SeesNewContent()
        {
            await SeedProjectDbAsync();

            // Force the session to cache a context before any writes
            var dbBefore = await _session.OpenAsync(_folder);
            Assert.False(await dbBefore.Volumes.AnyAsync());

            // Persist hierarchy through the session (same as BookReadingService now does)
            var dbForWrite = await _session.OpenAsync(_folder);
            var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol 1", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
            var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
            var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
            dbForWrite.Volumes.Add(vol);
            dbForWrite.Parts.Add(part);
            dbForWrite.Chapters.Add(ch);
            await dbForWrite.SaveChangesAsync();

            // Evict — simulates BookUseCases.ImportAsync evicting after write
            _session.Evict(_folder);

            // Read through a fresh context obtained via session
            var dbAfter = await _session.OpenAsync(_folder);
            Assert.True(await dbAfter.Volumes.AnyAsync());
            var volumes = await dbAfter.Volumes.ToListAsync();
            Assert.Single(volumes);
            Assert.Equal("Vol 1", volumes[0].Title);
        }

        [Fact]
        public async Task Evict_DisposesCachedContext_FreshContextReturnedAfter()
        {
            await SeedProjectDbAsync();

            var first = await _session.OpenAsync(_folder);
            _session.Evict(_folder);
            var second = await _session.OpenAsync(_folder);

            Assert.NotSame(first, second);
        }

        // Regression: adding/deleting a voice through BookCommandHandler must be visible to a
        // follow-up tracked read on the same session. The session caches one long-lived tracking
        // DbContext per folder; without eviction after a write, the tracker returns stale entities
        // (a deleted voice still counted in Character.Voices — the delete mutation uses ExecuteDelete
        // which bypasses the tracker). This drove the "voice chip doesn't update until you navigate
        // away and back" bug.

        [Fact]
        public async Task DeleteVoiceCommand_ThenTrackedRead_ReflectsDeletion()
        {
            await SeedProjectDbAsync();
            var (characterId, voiceIds) = await SeedCharacterWithVoicesAsync(count: 2);
            var handler = BuildCommandHandler();

            // Load into the cached tracking context first (this is what the UI does before deleting).
            Assert.Equal(2, await ReadTrackedVoiceCountAsync(characterId));

            await handler.ExecuteAsync(new DeleteVoiceCommand(_folder, voiceIds[0]));

            Assert.Equal(1, await ReadTrackedVoiceCountAsync(characterId));
        }

        [Fact]
        public async Task CreateVoiceCommand_ThenTrackedRead_ReflectsAddition()
        {
            await SeedProjectDbAsync();
            var (characterId, _) = await SeedCharacterWithVoicesAsync(count: 1);
            var handler = BuildCommandHandler();

            Assert.Equal(1, await ReadTrackedVoiceCountAsync(characterId));

            await handler.ExecuteAsync(new CreateVoiceCommand(_folder, characterId, "Second"));

            Assert.Equal(2, await ReadTrackedVoiceCountAsync(characterId));
        }

        private async Task<(Guid CharacterId, List<Guid> VoiceIds)> SeedCharacterWithVoicesAsync(int count)
        {
            var db = await _session.OpenAsync(_folder);
            var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            db.Characters.Add(character);
            var voiceIds = new List<Guid>();
            for (var i = 0; i < count; i++)
            {
                var voice = new VoiceEntity
                {
                    Id = Guid.NewGuid(),
                    CharacterId = character.Id,
                    Name = $"Voice {i}",
                    Source = VoiceSource.Uploaded,
                    CreatedUtc = DateTime.UtcNow.AddSeconds(i),
                };
                db.Voices.Add(voice);
                voiceIds.Add(voice.Id);
            }
            await db.SaveChangesAsync();
            _session.Evict(_folder); // start each test from a clean tracker, like a fresh circuit
            return (character.Id, voiceIds);
        }

        // Mirrors ProjectReader.GetCharactersWithAliasesAsync: tracked, Include(Voices).
        private async Task<int> ReadTrackedVoiceCountAsync(Guid characterId)
        {
            var db = await _session.OpenAsync(_folder);
            var character = await db.Characters
                .Include(c => c.Voices)
                .FirstAsync(c => c.Id == characterId);
            return character.Voices.Count;
        }

        /// <summary>
        /// The real command wiring, sharing this test's <see cref="ProjectDbSession"/> so the
        /// eviction the write side performs is the one the follow-up tracked read sees.
        /// </summary>
        private BookCommandHandler BuildCommandHandler()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = _tempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddSingleton(_fs);
            services.AddSingleton(_session);
            _services = services.BuildServiceProvider();
            return _services.GetRequiredService<BookCommandHandler>();
        }

        private ServiceProvider? _services;

        [Fact]
        public void BookReadingService_DoesNotConstructContextsOutsideSession()
        {
            var fields = typeof(BookReadingService)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            var hasFactory = fields.Any(f =>
                typeof(IProjectDbContextFactory).IsAssignableFrom(f.FieldType));

            Assert.False(hasFactory, "BookReadingService must not hold IProjectDbContextFactory; use ProjectDbSession instead.");
        }
    }
}
