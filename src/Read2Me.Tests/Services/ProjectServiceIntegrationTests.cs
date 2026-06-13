using System;
using System.IO;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// Integration tests for ProjectService using real file system (temp dir) and real SQLite.
    /// </summary>
    public class ProjectServiceIntegrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectService _svc;

        public ProjectServiceIntegrationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Read2MeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _svc = CreateService(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private static ProjectService CreateService(string workspaceDir)
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = workspaceDir }));
            return new ProjectService(fs, new ProjectDbContextProvider(), NullLogger<ProjectService>.Instance);
        }

        private static async Task<ProjectDbContext> OpenDbAsync(string folderPath)
        {
            var dbPath = Path.Combine(folderPath, "project.db");
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }

        // ---------------------------------------------------------------
        // GetProjects
        // ---------------------------------------------------------------

        [Fact]
        public void GetProjects_WhenWorkspaceNotExist_ReturnsEmpty()
        {
            var nonExistentDir = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N"));
            var svc = CreateService(nonExistentDir);

            var result = svc.GetProjects();

            Assert.Empty(result);
        }

        [Fact]
        public void GetProjects_WhenEmpty_ReturnsEmpty()
        {
            var result = _svc.GetProjects();
            Assert.Empty(result);
        }

        [Fact]
        public void GetProjects_WhenHasFolders_ReturnsSortedList()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "zebra"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "alpha"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "mango"));

            var result = _svc.GetProjects();

            Assert.Equal(["alpha", "mango", "zebra"], result);
        }

        // ---------------------------------------------------------------
        // CreateProjectAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task CreateProjectAsync_CreatesFolder_AndDbRecord()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3 });

            var folderName = await _svc.CreateProjectAsync(
                "My Book", "The Book Title", "Author Name",
                "book.txt", stream, BookFileType.Text);

            Assert.Equal("my-book", folderName);
            Assert.True(Directory.Exists(Path.Combine(_tempDir, folderName)));

            await using var db = await OpenDbAsync(Path.Combine(_tempDir, folderName));
            var project = await db.Projects.FirstOrDefaultAsync();
            Assert.NotNull(project);
            Assert.Equal("My Book", project.Title);
            Assert.Equal("The Book Title", project.BookTitle);
            Assert.Equal("Author Name", project.Author);
            Assert.Equal("book.txt", project.Filename);
            Assert.Equal(BookFileType.Text, project.Type);
        }

        [Fact]
        public async Task CreateProjectAsync_WhenDuplicate_Throws()
        {
            var stream1 = new MemoryStream(new byte[] { 1 });
            await _svc.CreateProjectAsync("My Book", "Title", "Author", "book.txt", stream1, BookFileType.Text);

            var stream2 = new MemoryStream(new byte[] { 2 });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _svc.CreateProjectAsync("My Book", "Title", "Author", "book.txt", stream2, BookFileType.Text));
        }

        [Fact]
        public async Task CreateProjectAsync_WhenEmptyTitle_Throws()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _svc.CreateProjectAsync("!!!", "Title", "Author", "book.txt", stream, BookFileType.Text));
        }

        // ---------------------------------------------------------------
        // GetProjectAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task GetProjectAsync_WhenNoDb_ReturnsNull()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "empty-project"));

            var result = await _svc.GetProjectAsync("empty-project");

            Assert.Null(result);
        }

        [Fact]
        public async Task GetProjectAsync_WhenExists_ReturnsProject()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Existing Book", "Book Title", "Auth", "file.txt", stream, BookFileType.Text);

            var project = await _svc.GetProjectAsync(folderName);

            Assert.NotNull(project);
            Assert.Equal("Existing Book", project.Title);
        }

        [Fact]
        public async Task GetProjectAsync_MigratesExistingDb()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Migrate Test", "Title", "Author", "file.txt", stream, BookFileType.Text);

            var svc2 = CreateService(_tempDir);
            var project = await svc2.GetProjectAsync(folderName);

            Assert.NotNull(project);
            Assert.Equal("Migrate Test", project.Title);
        }

        // ---------------------------------------------------------------
        // HasBookContentAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task HasBookContentAsync_WhenNoDb_ReturnsFalse()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "no-db-project"));

            var result = await _svc.HasBookContentAsync("no-db-project");

            Assert.False(result);
        }

        [Fact]
        public async Task HasBookContentAsync_WhenNoVolumes_ReturnsFalse()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Empty Content", "Title", "Author", "file.txt", stream, BookFileType.Text);

            var result = await _svc.HasBookContentAsync(folderName);

            Assert.False(result);
        }

        [Fact]
        public async Task HasBookContentAsync_WhenHasVolumes_ReturnsTrue()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Has Content", "Title", "Author", "file.txt", stream, BookFileType.Text);

            var folderPath = Path.Combine(_tempDir, folderName);
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Volumes.Add(new Volume
                {
                    Id = Guid.NewGuid(),
                    Title = "Volume 1",
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null)
                });
                await db.SaveChangesAsync();
            }

            var result = await _svc.HasBookContentAsync(folderName);

            Assert.True(result);
        }

        // ---------------------------------------------------------------
        // SaveCoverImageAsync / DeleteCoverImageAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SaveCoverImageAsync_SavesFileAndUpdatesDb()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Cover Book", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF };
            await _svc.SaveCoverImageAsync(folderName, "cover.jpg", new MemoryStream(imageBytes));

            var folderPath = Path.Combine(_tempDir, folderName);
            Assert.True(File.Exists(Path.Combine(folderPath, "cover.jpg")));

            var project = await _svc.GetProjectAsync(folderName);
            Assert.Equal("cover.jpg", project!.CoverImage);
        }

        [Fact]
        public async Task SaveCoverImageAsync_ReplacesExistingCover()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Replace Cover", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            await _svc.SaveCoverImageAsync(folderName, "cover1.jpg", new MemoryStream(new byte[] { 1 }));
            await _svc.SaveCoverImageAsync(folderName, "cover2.jpg", new MemoryStream(new byte[] { 2 }));

            var folderPath = Path.Combine(_tempDir, folderName);
            Assert.False(File.Exists(Path.Combine(folderPath, "cover1.jpg")));
            Assert.True(File.Exists(Path.Combine(folderPath, "cover2.jpg")));

            var project = await _svc.GetProjectAsync(folderName);
            Assert.Equal("cover2.jpg", project!.CoverImage);
        }

        [Fact]
        public async Task DeleteCoverImageAsync_DeletesFileAndClearsDb()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "Delete Cover", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            await _svc.SaveCoverImageAsync(folderName, "cover.jpg", new MemoryStream(new byte[] { 1 }));
            await _svc.DeleteCoverImageAsync(folderName);

            var folderPath = Path.Combine(_tempDir, folderName);
            Assert.False(File.Exists(Path.Combine(folderPath, "cover.jpg")));

            var project = await _svc.GetProjectAsync(folderName);
            Assert.Null(project!.CoverImage);
        }

        [Fact]
        public async Task DeleteCoverImageAsync_WhenNoCover_DoesNotThrow()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _svc.CreateProjectAsync(
                "No Cover", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            var ex = await Record.ExceptionAsync(() => _svc.DeleteCoverImageAsync(folderName));

            Assert.Null(ex);
        }
    }
}
