using System.Reflection;
using FractionalIndexing;
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
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectDbSessionConsistencyTests : IAsyncDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectDbSession _session;
        private readonly ProjectFolderId _folder;

        public ProjectDbSessionConsistencyTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "SessionConsistency_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = _tempDir }));
            _session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _folder = new ProjectFolderId("test-session");
        }

        public async ValueTask DisposeAsync()
        {
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
