using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Exceptions;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.IO;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectServiceIntegrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectService _writer;
        private readonly ProjectReader _reader;
        private readonly BookCommandHandler _cmd;

        public ProjectServiceIntegrationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Read2MeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = _tempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<ProjectService>();
            services.AddScoped(sp => NullLogger<ProjectService>.Instance);
            services.AddScoped<ProjectReader>();
            services.AddScoped(sp => NullLogger<ProjectReader>.Instance);
            var sp = services.BuildServiceProvider();

            _writer = sp.GetRequiredService<ProjectService>();
            _reader = sp.GetRequiredService<ProjectReader>();
            _cmd = sp.GetRequiredService<BookCommandHandler>();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private static ProjectReader CreateReader(string workspaceDir)
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = workspaceDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            return new ProjectReader(session, NullLogger<ProjectReader>.Instance);
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
            var reader = CreateReader(nonExistentDir);

            var result = reader.GetProjects();

            Assert.Empty(result);
        }

        [Fact]
        public void GetProjects_WhenEmpty_ReturnsEmpty()
        {
            var result = _reader.GetProjects();
            Assert.Empty(result);
        }

        [Fact]
        public void GetProjects_WhenHasFolders_ReturnsSortedList()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "zebra"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "alpha"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "mango"));

            var result = _reader.GetProjects();

            Assert.Equal(["alpha", "mango", "zebra"], result);
        }

        // ---------------------------------------------------------------
        // CreateProjectAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task CreateProjectAsync_CreatesFolder_AndDbRecord()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3 });

            var folderName = await _writer.CreateProjectAsync(
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
            await _writer.CreateProjectAsync("My Book", "Title", "Author", "book.txt", stream1, BookFileType.Text);

            var stream2 = new MemoryStream(new byte[] { 2 });
            await Assert.ThrowsAsync<ProjectAlreadyExistsException>(() =>
                _writer.CreateProjectAsync("My Book", "Title", "Author", "book.txt", stream2, BookFileType.Text));
        }

        [Fact]
        public async Task CreateProjectAsync_WhenEmptyTitle_Throws()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _writer.CreateProjectAsync("!!!", "Title", "Author", "book.txt", stream, BookFileType.Text));
        }

        // ---------------------------------------------------------------
        // GetProjectAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task GetProjectAsync_WhenNoDb_ReturnsNull()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "empty-project"));

            var result = await _reader.GetProjectAsync("empty-project");

            Assert.Null(result);
        }

        [Fact]
        public async Task GetProjectAsync_WhenExists_ReturnsProject()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "Existing Book", "Book Title", "Auth", "file.txt", stream, BookFileType.Text);

            var project = await _reader.GetProjectAsync(folderName);

            Assert.NotNull(project);
            Assert.Equal("Existing Book", project.Title);
        }

        [Fact]
        public async Task GetProjectAsync_MigratesExistingDb()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "Migrate Test", "Title", "Author", "file.txt", stream, BookFileType.Text);

            var reader2 = CreateReader(_tempDir);
            var project = await reader2.GetProjectAsync(folderName);

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

            var result = await _reader.HasBookContentAsync("no-db-project");

            Assert.False(result);
        }

        [Fact]
        public async Task HasBookContentAsync_WhenNoVolumes_ReturnsFalse()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "Empty Content", "Title", "Author", "file.txt", stream, BookFileType.Text);

            var result = await _reader.HasBookContentAsync(folderName);

            Assert.False(result);
        }

        [Fact]
        public async Task HasBookContentAsync_WhenHasVolumes_ReturnsTrue()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
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

            var result = await _reader.HasBookContentAsync(folderName);

            Assert.True(result);
        }

        // ---------------------------------------------------------------
        // SaveCoverImageAsync / DeleteCoverImageAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SaveCoverImageAsync_SavesFileAndUpdatesDb()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "Cover Book", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF };
            await _writer.SaveCoverImageAsync(folderName, "cover.jpg", new MemoryStream(imageBytes));

            var folderPath = Path.Combine(_tempDir, folderName);
            Assert.True(File.Exists(Path.Combine(folderPath, "cover.jpg")));

            var project = await _reader.GetProjectAsync(folderName);
            Assert.Equal("cover.jpg", project!.CoverImage);
        }

        [Fact]
        public async Task SaveCoverImageAsync_ReplacesExistingCover()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "Replace Cover", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            await _writer.SaveCoverImageAsync(folderName, "cover1.jpg", new MemoryStream(new byte[] { 1 }));
            await _writer.SaveCoverImageAsync(folderName, "cover2.jpg", new MemoryStream(new byte[] { 2 }));

            var folderPath = Path.Combine(_tempDir, folderName);
            Assert.False(File.Exists(Path.Combine(folderPath, "cover1.jpg")));
            Assert.True(File.Exists(Path.Combine(folderPath, "cover2.jpg")));

            var project = await _reader.GetProjectAsync(folderName);
            Assert.Equal("cover2.jpg", project!.CoverImage);
        }

        [Fact]
        public async Task DeleteCoverImageAsync_DeletesFileAndClearsDb()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "Delete Cover", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            await _writer.SaveCoverImageAsync(folderName, "cover.jpg", new MemoryStream(new byte[] { 1 }));
            await _writer.DeleteCoverImageAsync(folderName);

            var folderPath = Path.Combine(_tempDir, folderName);
            Assert.False(File.Exists(Path.Combine(folderPath, "cover.jpg")));

            var project = await _reader.GetProjectAsync(folderName);
            Assert.Null(project!.CoverImage);
        }

        [Fact]
        public async Task DeleteCoverImageAsync_WhenNoCover_DoesNotThrow()
        {
            var bookStream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync(
                "No Cover", "Title", "Author", "file.txt", bookStream, BookFileType.Text);

            var ex = await Record.ExceptionAsync(() => _writer.DeleteCoverImageAsync(folderName));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // UpdateVolumeTitleAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task UpdateVolumeTitleAsync_UpdatesTitle()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Vol Update", "Title", "Author", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);
            var volumeId = Guid.NewGuid();
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Volumes.Add(new Volume { Id = volumeId, Title = "Old Title", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new UpdateVolumeTitleCommand(folderName, volumeId, "New Title"));

            await using var db2 = await OpenDbAsync(folderPath);
            var vol = await db2.Volumes.FindAsync(volumeId);
            Assert.Equal("New Title", vol!.Title);
        }

        [Fact]
        public async Task UpdateVolumeTitleAsync_WhenNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Vol NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new UpdateVolumeTitleCommand(folderName, Guid.NewGuid(), "X")));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // UpdatePartTitleAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task UpdatePartTitleAsync_UpdatesTitle()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Part Update", "Title", "Author", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);
            var volumeId = Guid.NewGuid();
            var partId = Guid.NewGuid();
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Volumes.Add(new Volume { Id = volumeId, Title = "V", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.Parts.Add(new Part { Id = partId, VolumeId = volumeId, Title = "Old Part", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new UpdatePartTitleCommand(folderName, partId, "New Part"));

            await using var db2 = await OpenDbAsync(folderPath);
            var part = await db2.Parts.FindAsync(partId);
            Assert.Equal("New Part", part!.Title);
        }

        [Fact]
        public async Task UpdatePartTitleAsync_WhenNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Part NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new UpdatePartTitleCommand(folderName, Guid.NewGuid(), "X")));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // UpdateChapterTitleAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task UpdateChapterTitleAsync_UpdatesTitle()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Ch Update", "Title", "Author", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);
            var volumeId = Guid.NewGuid();
            var partId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Volumes.Add(new Volume { Id = volumeId, Title = "V", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.Parts.Add(new Part { Id = partId, VolumeId = volumeId, Title = "P", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.Chapters.Add(new Chapter { Id = chapterId, PartId = partId, Title = "Old Chapter", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new UpdateChapterTitleCommand(folderName, chapterId, "New Chapter"));

            await using var db2 = await OpenDbAsync(folderPath);
            var ch = await db2.Chapters.FindAsync(chapterId);
            Assert.Equal("New Chapter", ch!.Title);
        }

        [Fact]
        public async Task UpdateChapterTitleAsync_WhenNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Ch NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new UpdateChapterTitleCommand(folderName, Guid.NewGuid(), "X")));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // UpdateParagraphItemTextAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task UpdateParagraphItemTextAsync_UpdatesText()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Item Update", "Title", "Author", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);
            var volumeId = Guid.NewGuid();
            var partId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            var paragraphId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            await using (var db = await OpenDbAsync(folderPath))
            {
                db.Volumes.Add(new Volume { Id = volumeId, Title = "V", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.Parts.Add(new Part { Id = partId, VolumeId = volumeId, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.Chapters.Add(new Chapter { Id = chapterId, PartId = partId, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.Paragraphs.Add(new Paragraph { Id = paragraphId, ChapterId = chapterId, Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                db.ParagraphItems.Add(new ParagraphItem { Id = itemId, ParagraphId = paragraphId, Text = "Old text", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new UpdateParagraphItemTextCommand(folderName, itemId, "New text"));

            await using var db2 = await OpenDbAsync(folderPath);
            var item = await db2.ParagraphItems.FindAsync(itemId);
            Assert.Equal("New text", item!.Text);
        }

        [Fact]
        public async Task UpdateParagraphItemTextAsync_WhenNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Item NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new UpdateParagraphItemTextCommand(folderName, Guid.NewGuid(), "X")));

            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // Helpers for split tests
        // ---------------------------------------------------------------

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        // ---------------------------------------------------------------
        // SplitVolumeAsync (splits at Part boundary — creates new Volume)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitVolumeAsync_CreatesNewVolume_MovesSelectedAndTrailingParts()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVol", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid vol1Id, part1Id, part2Id, part3Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                string vk = Key();
                var vol1 = new Volume { Id = vol1Id = Guid.NewGuid(), Title = "Vol1", Order = vk };
                db.Volumes.Add(vol1);

                string pk1 = Key(), pk2 = Key(pk1), pk3 = Key(pk2);
                db.Parts.AddRange(
                    new Part { Id = part1Id = Guid.NewGuid(), VolumeId = vol1Id, Title = "P1", Order = pk1 },
                    new Part { Id = part2Id = Guid.NewGuid(), VolumeId = vol1Id, Title = "P2", Order = pk2 },
                    new Part { Id = part3Id = Guid.NewGuid(), VolumeId = vol1Id, Title = "P3", Order = pk3 });
                await db.SaveChangesAsync();
            }

            // Split at Part2: Vol1 keeps P1, new Volume gets P2 + P3
            await _cmd.ExecuteAsync(new SplitAtPartCommand(folderName, part2Id, "Vol2"));

            await using var verify = await OpenDbAsync(folderPath);
            var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
            Assert.Equal(2, volumes.Count);
            Assert.Equal("Vol1", volumes[0].Title);
            Assert.Equal("Vol2", volumes[1].Title);

            var oldVolParts = await verify.Parts.Where(p => p.VolumeId == volumes[0].Id).OrderBy(p => p.Order).ToListAsync();
            var newVolParts = await verify.Parts.Where(p => p.VolumeId == volumes[1].Id).OrderBy(p => p.Order).ToListAsync();
            Assert.Equal([part1Id], oldVolParts.Select(p => p.Id));
            Assert.Equal([part2Id, part3Id], newVolParts.Select(p => p.Id));
        }

        [Fact]
        public async Task SplitVolumeAsync_FirstPart_NewVolumeGetsAllParts()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVolFirst", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid vol1Id, part1Id, part2Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol1 = new Volume { Id = vol1Id = Guid.NewGuid(), Title = "Vol1", Order = Key() };
                db.Volumes.Add(vol1);
                string pk1 = Key(), pk2 = Key(pk1);
                db.Parts.AddRange(
                    new Part { Id = part1Id = Guid.NewGuid(), VolumeId = vol1Id, Order = pk1 },
                    new Part { Id = part2Id = Guid.NewGuid(), VolumeId = vol1Id, Order = pk2 });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new SplitAtPartCommand(folderName, part1Id, "NewVol"));

            await using var verify = await OpenDbAsync(folderPath);
            var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
            Assert.Equal(2, volumes.Count);
            // New volume after original; gets both parts (all parts move when splitting at first part)
            var newVolParts = await verify.Parts.Where(p => p.VolumeId == volumes[1].Id).ToListAsync();
            Assert.Equal(2, newVolParts.Count);
        }

        [Fact]
        public async Task SplitVolumeAsync_WhenPartNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVolNF", "T", "A", "f.txt", stream, BookFileType.Text);
            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new SplitAtPartCommand(folderName, Guid.NewGuid(), null)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // SplitPartAsync (splits at Chapter boundary — creates new Part)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitPartAsync_CreatesNewPart_MovesSelectedAndTrailingChapters()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitPart", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid volId, part1Id, ch1Id, ch2Id, ch3Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol = new Volume { Id = volId = Guid.NewGuid(), Title = "V", Order = Key() };
                db.Volumes.Add(vol);
                string pk1 = Key(), pk2 = Key(pk1);
                var part1 = new Part { Id = part1Id = Guid.NewGuid(), VolumeId = volId, Title = "P1", Order = pk1 };
                db.Parts.Add(part1);
                string ck1 = Key(), ck2 = Key(ck1), ck3 = Key(ck2);
                db.Chapters.AddRange(
                    new Chapter { Id = ch1Id = Guid.NewGuid(), PartId = part1Id, Title = "Ch1", Order = ck1 },
                    new Chapter { Id = ch2Id = Guid.NewGuid(), PartId = part1Id, Title = "Ch2", Order = ck2 },
                    new Chapter { Id = ch3Id = Guid.NewGuid(), PartId = part1Id, Title = "Ch3", Order = ck3 });
                await db.SaveChangesAsync();
            }

            // Split at Ch2: P1 keeps Ch1, new Part gets Ch2 + Ch3
            await _cmd.ExecuteAsync(new SplitAtChapterCommand(folderName, ch2Id, "P2"));

            await using var verify = await OpenDbAsync(folderPath);
            var parts = await verify.Parts.OrderBy(p => p.Order).ToListAsync();
            Assert.Equal(2, parts.Count);
            Assert.Equal("P1", parts[0].Title);
            Assert.Equal("P2", parts[1].Title);

            var oldPartChs = await verify.Chapters.Where(c => c.PartId == parts[0].Id).OrderBy(c => c.Order).ToListAsync();
            var newPartChs = await verify.Chapters.Where(c => c.PartId == parts[1].Id).OrderBy(c => c.Order).ToListAsync();
            Assert.Equal([ch1Id], oldPartChs.Select(c => c.Id));
            Assert.Equal([ch2Id, ch3Id], newPartChs.Select(c => c.Id));
        }

        [Fact]
        public async Task SplitPartAsync_WhenChapterNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitPartNF", "T", "A", "f.txt", stream, BookFileType.Text);
            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new SplitAtChapterCommand(folderName, Guid.NewGuid(), null)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // SplitChapterAsync (splits at Paragraph boundary — creates new Chapter)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitChapterAsync_CreatesNewChapter_MovesSelectedAndTrailingParagraphs()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitCh", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid partId, ch1Id, pg1Id, pg2Id, pg3Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
                db.Volumes.Add(vol);
                var part = new Part { Id = partId = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
                db.Parts.Add(part);
                var ch1 = new Chapter { Id = ch1Id = Guid.NewGuid(), PartId = partId, Title = "Ch1", Order = Key() };
                db.Chapters.Add(ch1);
                string pgk1 = Key(), pgk2 = Key(pgk1), pgk3 = Key(pgk2);
                db.Paragraphs.AddRange(
                    new Paragraph { Id = pg1Id = Guid.NewGuid(), ChapterId = ch1Id, Order = pgk1 },
                    new Paragraph { Id = pg2Id = Guid.NewGuid(), ChapterId = ch1Id, Order = pgk2 },
                    new Paragraph { Id = pg3Id = Guid.NewGuid(), ChapterId = ch1Id, Order = pgk3 });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new SplitAtParagraphCommand(folderName, pg2Id, "Ch2"));

            await using var verify = await OpenDbAsync(folderPath);
            var chapters = await verify.Chapters.OrderBy(c => c.Order).ToListAsync();
            Assert.Equal(2, chapters.Count);
            Assert.Equal("Ch1", chapters[0].Title);
            Assert.Equal("Ch2", chapters[1].Title);

            var oldChPgs = await verify.Paragraphs.Where(p => p.ChapterId == chapters[0].Id).OrderBy(p => p.Order).ToListAsync();
            var newChPgs = await verify.Paragraphs.Where(p => p.ChapterId == chapters[1].Id).OrderBy(p => p.Order).ToListAsync();
            Assert.Equal([pg1Id], oldChPgs.Select(p => p.Id));
            Assert.Equal([pg2Id, pg3Id], newChPgs.Select(p => p.Id));
        }

        [Fact]
        public async Task SplitChapterAsync_WhenParagraphNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitChNF", "T", "A", "f.txt", stream, BookFileType.Text);
            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new SplitAtParagraphCommand(folderName, Guid.NewGuid(), null)));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // SplitParagraphAsync (splits at Item boundary — creates new Paragraph)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitParagraphAsync_CreatesNewParagraph_MovesSelectedAndTrailingItems()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitPg", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid chapterId, pg1Id, item1Id, item2Id, item3Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
                db.Volumes.Add(vol);
                var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
                db.Parts.Add(part);
                var ch = new Chapter { Id = chapterId = Guid.NewGuid(), PartId = part.Id, Order = Key() };
                db.Chapters.Add(ch);
                var pg1 = new Paragraph { Id = pg1Id = Guid.NewGuid(), ChapterId = chapterId, Order = Key() };
                db.Paragraphs.Add(pg1);
                string ik1 = Key(), ik2 = Key(ik1), ik3 = Key(ik2);
                db.ParagraphItems.AddRange(
                    new ParagraphItem { Id = item1Id = Guid.NewGuid(), ParagraphId = pg1Id, Order = ik1, Text = "a" },
                    new ParagraphItem { Id = item2Id = Guid.NewGuid(), ParagraphId = pg1Id, Order = ik2, Text = "b" },
                    new ParagraphItem { Id = item3Id = Guid.NewGuid(), ParagraphId = pg1Id, Order = ik3, Text = "c" });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new SplitAtItemCommand(folderName, item2Id));

            await using var verify = await OpenDbAsync(folderPath);
            var paragraphs = await verify.Paragraphs.OrderBy(p => p.Order).ToListAsync();
            Assert.Equal(2, paragraphs.Count);

            var oldPgItems = await verify.ParagraphItems.Where(i => i.ParagraphId == paragraphs[0].Id).OrderBy(i => i.Order).ToListAsync();
            var newPgItems = await verify.ParagraphItems.Where(i => i.ParagraphId == paragraphs[1].Id).OrderBy(i => i.Order).ToListAsync();
            Assert.Equal([item1Id], oldPgItems.Select(i => i.Id));
            Assert.Equal([item2Id, item3Id], newPgItems.Select(i => i.Id));
        }

        [Fact]
        public async Task SplitParagraphAsync_WhenItemNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitPgNF", "T", "A", "f.txt", stream, BookFileType.Text);
            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new SplitAtItemCommand(folderName, Guid.NewGuid())));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // SplitParagraphItemAsync (Split Line — delegates to SplitParagraphAsync)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitParagraphItemAsync_BehavesSameAsSplitParagraph()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitLine", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid chapterId, pg1Id, item1Id, item2Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol = new Volume { Id = Guid.NewGuid(), Title = "V", Order = Key() };
                db.Volumes.Add(vol);
                var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = Key() };
                db.Parts.Add(part);
                var ch = new Chapter { Id = chapterId = Guid.NewGuid(), PartId = part.Id, Order = Key() };
                db.Chapters.Add(ch);
                var pg1 = new Paragraph { Id = pg1Id = Guid.NewGuid(), ChapterId = chapterId, Order = Key() };
                db.Paragraphs.Add(pg1);
                string ik1 = Key(), ik2 = Key(ik1);
                db.ParagraphItems.AddRange(
                    new ParagraphItem { Id = item1Id = Guid.NewGuid(), ParagraphId = pg1Id, Order = ik1, Text = "x" },
                    new ParagraphItem { Id = item2Id = Guid.NewGuid(), ParagraphId = pg1Id, Order = ik2, Text = "y" });
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new SplitAtItemCommand(folderName, item2Id));

            await using var verify = await OpenDbAsync(folderPath);
            var paragraphs = await verify.Paragraphs.OrderBy(p => p.Order).ToListAsync();
            Assert.Equal(2, paragraphs.Count);

            var newPgItems = await verify.ParagraphItems.Where(i => i.ParagraphId == paragraphs[1].Id).ToListAsync();
            Assert.Single(newPgItems);
            Assert.Equal(item2Id, newPgItems[0].Id);
        }

        [Fact]
        public async Task SplitParagraphItemAsync_WhenItemNotFound_DoesNotThrow()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitLineNF", "T", "A", "f.txt", stream, BookFileType.Text);
            var ex = await Record.ExceptionAsync(() => _cmd.ExecuteAsync(new SplitAtItemCommand(folderName, Guid.NewGuid())));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // Split ordering — new entity always ordered after original
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitVolumeAsync_NewVolumeOrder_IsAfterOriginal()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVolOrder", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            Guid vol1Id, part1Id;
            await using (var db = await OpenDbAsync(folderPath))
            {
                var vol1 = new Volume { Id = vol1Id = Guid.NewGuid(), Title = "Vol1", Order = Key() };
                db.Volumes.Add(vol1);
                var part1 = new Part { Id = part1Id = Guid.NewGuid(), VolumeId = vol1Id, Order = Key() };
                db.Parts.Add(part1);
                await db.SaveChangesAsync();
            }

            await _cmd.ExecuteAsync(new SplitAtPartCommand(folderName, part1Id, "NewVol"));

            await using var verify = await OpenDbAsync(folderPath);
            var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
            Assert.Equal(2, volumes.Count);
            Assert.True(string.Compare(volumes[0].Order, volumes[1].Order, StringComparison.Ordinal) < 0);
            Assert.Equal("NewVol", volumes[1].Title);
        }
    }
}
