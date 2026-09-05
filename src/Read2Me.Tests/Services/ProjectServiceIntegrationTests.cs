using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Exceptions;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectServiceIntegrationTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectService _writer;
        private readonly ProjectReader _reader;
        private readonly BookMutations _mutations;

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
            _mutations = sp.GetRequiredService<BookMutations>();
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

        private static Task<ProjectDbContext> OpenDbAsync(string folderPath)
        {
            var dbPath = Path.Combine(folderPath, "project.db");
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            db.Database.Migrate();
            return Task.FromResult(db);
        }

        private BookHierarchyBuilder BuilderFor(string folderPath) =>
            new BookHierarchyBuilder(() => OpenDbAsync(folderPath));

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

            var b = BuilderFor(Path.Combine(_tempDir, folderName));
            await b.AddVolume("vol").AddHierarchyAsync();

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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol").AddHierarchyAsync();

            await _mutations.CommitAsync(new UpdateVolumeTitleMutation(folderName, b.VolumeId("vol"), "New Title"));

            await using var db2 = await OpenDbAsync(folderPath);
            var vol = await db2.Volumes.FindAsync(b.VolumeId("vol"));
            Assert.Equal("New Title", vol!.Title);
        }

        [Fact]
        public async Task UpdateVolumeTitleAsync_WhenNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Vol NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new UpdateVolumeTitleMutation(folderName, Guid.NewGuid(), "X"))).Reason);

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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v.AddPart("part")).AddHierarchyAsync();

            await _mutations.CommitAsync(new UpdatePartTitleMutation(folderName, b.PartId("part"), "New Part"));

            await using var db2 = await OpenDbAsync(folderPath);
            var part = await db2.Parts.FindAsync(b.PartId("part"));
            Assert.Equal("New Part", part!.Title);
        }

        [Fact]
        public async Task UpdatePartTitleAsync_WhenNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Part NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new UpdatePartTitleMutation(folderName, Guid.NewGuid(), "X"))).Reason);

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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v.AddChapter("ch")).AddHierarchyAsync();

            await _mutations.CommitAsync(new UpdateChapterTitleMutation(folderName, b.ChapterId("ch"), "New Chapter"));

            await using var db2 = await OpenDbAsync(folderPath);
            var ch = await db2.Chapters.FindAsync(b.ChapterId("ch"));
            Assert.Equal("New Chapter", ch!.Title);
        }

        [Fact]
        public async Task UpdateChapterTitleAsync_WhenNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Ch NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new UpdateChapterTitleMutation(folderName, Guid.NewGuid(), "X"))).Reason);

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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph(configure: p => p.AddNarration("item", "Old text"))))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new UpdateParagraphItemTextMutation(folderName, b.ItemId("item"), "New text"));

            await using var db2 = await OpenDbAsync(folderPath);
            var item = await db2.ParagraphItems.FindAsync(b.ItemId("item"));
            Assert.Equal("New text", item!.Text);
        }

        [Fact]
        public async Task UpdateParagraphItemTextAsync_WhenNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("Item NotFound", "Title", "Author", "f.txt", stream, BookFileType.Text);

            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new UpdateParagraphItemTextMutation(folderName, Guid.NewGuid(), "X"))).Reason);

        }

        // ---------------------------------------------------------------
        // SplitVolumeAsync (splits at Part boundary — creates new Volume)
        // ---------------------------------------------------------------

        [Fact]
        public async Task SplitVolumeAsync_CreatesNewVolume_MovesSelectedAndTrailingParts()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVol", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol1", v => v
                .AddPart("p1")
                .AddPart("p2")
                .AddPart("p3"))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtPartMutation(folderName, b.PartId("p2"), "Vol2"));

            await using var verify = await OpenDbAsync(folderPath);
            var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
            Assert.Equal(2, volumes.Count);
            Assert.Equal("vol1", volumes[0].Title);
            Assert.Equal("Vol2", volumes[1].Title);

            var oldVolParts = await verify.Parts.Where(p => p.VolumeId == volumes[0].Id).OrderBy(p => p.Order).ToListAsync();
            var newVolParts = await verify.Parts.Where(p => p.VolumeId == volumes[1].Id).OrderBy(p => p.Order).ToListAsync();
            Assert.Equal([b.PartId("p1")], oldVolParts.Select(p => p.Id));
            Assert.Equal([b.PartId("p2"), b.PartId("p3")], newVolParts.Select(p => p.Id));
        }

        [Fact]
        public async Task SplitVolumeAsync_FirstPart_NewVolumeGetsAllParts()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVolFirst", "T", "A", "f.txt", stream, BookFileType.Text);
            var folderPath = Path.Combine(_tempDir, folderName);

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol1", v => v
                .AddPart("p1")
                .AddPart("p2"))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtPartMutation(folderName, b.PartId("p1"), "NewVol"));

            await using var verify = await OpenDbAsync(folderPath);
            var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
            Assert.Equal(2, volumes.Count);
            var newVolParts = await verify.Parts.Where(p => p.VolumeId == volumes[1].Id).ToListAsync();
            Assert.Equal(2, newVolParts.Count);
        }

        [Fact]
        public async Task SplitVolumeAsync_WhenPartNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitVolNF", "T", "A", "f.txt", stream, BookFileType.Text);
            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new SplitAtPartMutation(folderName, Guid.NewGuid(), null))).Reason);
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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v
                .AddPart("p1", p => p
                    .AddChapter("ch1")
                    .AddChapter("ch2")
                    .AddChapter("ch3")))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtChapterMutation(folderName, b.ChapterId("ch2"), "P2"));

            await using var verify = await OpenDbAsync(folderPath);
            var parts = await verify.Parts.OrderBy(p => p.Order).ToListAsync();
            Assert.Equal(2, parts.Count);
            Assert.Equal("p1", parts[0].Title);
            Assert.Equal("P2", parts[1].Title);

            var oldPartChs = await verify.Chapters.Where(c => c.PartId == parts[0].Id).OrderBy(c => c.Order).ToListAsync();
            var newPartChs = await verify.Chapters.Where(c => c.PartId == parts[1].Id).OrderBy(c => c.Order).ToListAsync();
            Assert.Equal([b.ChapterId("ch1")], oldPartChs.Select(c => c.Id));
            Assert.Equal([b.ChapterId("ch2"), b.ChapterId("ch3")], newPartChs.Select(c => c.Id));
        }

        [Fact]
        public async Task SplitPartAsync_WhenChapterNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitPartNF", "T", "A", "f.txt", stream, BookFileType.Text);
            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new SplitAtChapterMutation(folderName, Guid.NewGuid(), null))).Reason);
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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v
                .AddChapter("ch1", c => c
                    .AddParagraph("pg1", configure: p => p.AddNarration("i1"))
                    .AddParagraph("pg2", configure: p => p.AddNarration("i2"))
                    .AddParagraph("pg3", configure: p => p.AddNarration("i3"))))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtParagraphMutation(folderName, b.ParagraphId("pg2"), "Ch2"));

            await using var verify = await OpenDbAsync(folderPath);
            var chapters = await verify.Chapters.OrderBy(c => c.Order).ToListAsync();
            Assert.Equal(2, chapters.Count);
            Assert.Equal("ch1", chapters[0].Title);
            Assert.Equal("Ch2", chapters[1].Title);

            var oldChPgs = await verify.Paragraphs.Where(p => p.ChapterId == chapters[0].Id).OrderBy(p => p.Order).ToListAsync();
            var newChPgs = await verify.Paragraphs.Where(p => p.ChapterId == chapters[1].Id).OrderBy(p => p.Order).ToListAsync();
            Assert.Equal([b.ParagraphId("pg1")], oldChPgs.Select(p => p.Id));
            Assert.Equal([b.ParagraphId("pg2"), b.ParagraphId("pg3")], newChPgs.Select(p => p.Id));
        }

        [Fact]
        public async Task SplitChapterAsync_WhenParagraphNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitChNF", "T", "A", "f.txt", stream, BookFileType.Text);
            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new SplitAtParagraphMutation(folderName, Guid.NewGuid(), null))).Reason);
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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v
                .AddChapter(configure: c => c
                    .AddParagraph("pg1", p => p
                        .AddNarration("item1", "a")
                        .AddNarration("item2", "b")
                        .AddNarration("item3", "c"))))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtItemMutation(folderName, b.ItemId("item2")));

            await using var verify = await OpenDbAsync(folderPath);
            var paragraphs = await verify.Paragraphs.OrderBy(p => p.Order).ToListAsync();
            Assert.Equal(2, paragraphs.Count);

            var oldPgItems = await verify.ParagraphItems.Where(i => i.ParagraphId == paragraphs[0].Id).OrderBy(i => i.Order).ToListAsync();
            var newPgItems = await verify.ParagraphItems.Where(i => i.ParagraphId == paragraphs[1].Id).OrderBy(i => i.Order).ToListAsync();
            Assert.Equal([b.ItemId("item1")], oldPgItems.Select(i => i.Id));
            Assert.Equal([b.ItemId("item2"), b.ItemId("item3")], newPgItems.Select(i => i.Id));
        }

        [Fact]
        public async Task SplitParagraphAsync_WhenItemNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitPgNF", "T", "A", "f.txt", stream, BookFileType.Text);
            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new SplitAtItemMutation(folderName, Guid.NewGuid()))).Reason);
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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol", v => v
                .AddChapter(configure: c => c
                    .AddParagraph("pg1", p => p
                        .AddNarration("item1", "x")
                        .AddNarration("item2", "y"))))
                .AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtItemMutation(folderName, b.ItemId("item2")));

            await using var verify = await OpenDbAsync(folderPath);
            var paragraphs = await verify.Paragraphs.OrderBy(p => p.Order).ToListAsync();
            Assert.Equal(2, paragraphs.Count);

            var newPgItems = await verify.ParagraphItems.Where(i => i.ParagraphId == paragraphs[1].Id).ToListAsync();
            Assert.Single(newPgItems);
            Assert.Equal(b.ItemId("item2"), newPgItems[0].Id);
        }

        [Fact]
        public async Task SplitParagraphItemAsync_WhenItemNotFound_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1 });
            var folderName = await _writer.CreateProjectAsync("SplitLineNF", "T", "A", "f.txt", stream, BookFileType.Text);
            // A gesture naming a node the Book does not contain is refused, not silently applied.
            Assert.Equal(BookMutationRejection.NotFound,
                Assert.IsType<BookMutationOutcome.Rejected>(await _mutations.CommitAsync(new SplitAtItemMutation(folderName, Guid.NewGuid()))).Reason);
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

            var b = BuilderFor(folderPath);
            await b.AddVolume("vol1", v => v.AddPart("p1")).AddHierarchyAsync();

            await _mutations.CommitAsync(new SplitAtPartMutation(folderName, b.PartId("p1"), "NewVol"));

            await using var verify = await OpenDbAsync(folderPath);
            var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
            Assert.Equal(2, volumes.Count);
            Assert.True(string.Compare(volumes[0].Order, volumes[1].Order, StringComparison.Ordinal) < 0);
            Assert.Equal("NewVol", volumes[1].Title);
        }
    }
}
