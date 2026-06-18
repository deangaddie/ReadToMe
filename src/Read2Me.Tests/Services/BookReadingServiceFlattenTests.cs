using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Books;
using Read2Me.Services.IO;
using Xunit;

namespace Read2Me.Tests.Services;

// Maps any folder name to a fixed real directory on disk so the service finds the DB and files.
file sealed class FixedPathFileSystem(string fixedPath) : IFileSystem
{
    public IReadOnlyList<string> ListProjectFolders() => [];
    public bool ProjectFolderExists(string name) => true;
    public string GetProjectFolderPath(string name) => fixedPath;
    public void CreateProjectFolder(string name) { }
    public void DeleteProjectFolder(string name) { }
    public bool FileExists(string path) => File.Exists(path);
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);
    public async Task WriteFileAsync(string path, Stream source)
    {
        await using var f = File.Create(path);
        await source.CopyToAsync(f);
    }

    public Task WriteAllLinesAsync(string path, System.Collections.Generic.IEnumerable<string> lines) => File.WriteAllLinesAsync(path, lines);
}

public class BookReadingServiceFlattenTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly ProjectDbContext _db;
    private readonly ProjectDbSession _session;
    private readonly BookReadingService _sut;
    private const string FolderName = "test";

    public BookReadingServiceFlattenTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Read2MeFlattenTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var dbPath = Path.Combine(_tempDir, "project.db");
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=false")
            .Options;
        _db = new ProjectDbContext(options);
        _db.Database.Migrate();

        _session = new ProjectDbSession(
            new FixedPathFileSystem(_tempDir),
            new ProjectDbContextProvider(),
            NullLogger<ProjectDbSession>.Instance);

        _sut = new BookReadingService(
            _session,
            new EpubFileReader(NullLogger<EpubFileReader>.Instance),
            new TextFileReader(NullLogger<TextFileReader>.Instance),
            new BookContentPersister(),
            NullLogger<BookReadingService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private async Task SeedDbProjectAsync(string filename, BookFileType type)
    {
        _db.Projects.Add(new Project
        {
            Id = Guid.NewGuid(),
            Title = "Test Book",
            Filename = filename,
            Type = type,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedParagraphsAsync(params string[][] itemsPerParagraph)
    {
        var vol = new Volume { Id = Guid.NewGuid(), Title = "Vol 1", Order = OrderKeyGenerator.GenerateKeyBetween(null, null) };
        _db.Volumes.Add(vol);
        var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Title = null, Order = OrderKeyGenerator.GenerateKeyBetween(vol.Order, null) };
        _db.Parts.Add(part);
        var ch = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Title = "Ch 1", Order = OrderKeyGenerator.GenerateKeyBetween(part.Order, null) };
        _db.Chapters.Add(ch);

        string? prev = ch.Order;
        foreach (var items in itemsPerParagraph)
        {
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch.Id, Order = OrderKeyGenerator.GenerateKeyBetween(prev, null) };
            _db.Paragraphs.Add(para);
            prev = para.Order;
            foreach (var text in items)
            {
                _db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(prev, null),
                    ItemType = Read2Me.Data.Enums.ParagraphItemType.Narration,
                    Text = text,
                });
                prev = _db.ParagraphItems.Local.Last().Order;
            }
        }

        await _db.SaveChangesAsync();
    }

    // ── FlattenFromDbAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task FlattenFromDb_MultipleItemsPerParagraph_JoinedIntoOneLine()
    {
        await SeedParagraphsAsync(
            ["He said,", " \"Hello.\""],
            ["She replied."]
        );

        var lines = await _sut.FlattenFromDbAsync(FolderName);

        Assert.Equal(2, lines.Count);
        Assert.Equal("He said,  \"Hello.\"", lines[0]);
        Assert.Equal("She replied.", lines[1]);
    }

    [Fact]
    public async Task FlattenFromDb_SingleItemPerParagraph_ReturnedAsIs()
    {
        await SeedParagraphsAsync(
            ["First paragraph."],
            ["Second paragraph."],
            ["Third paragraph."]
        );

        var lines = await _sut.FlattenFromDbAsync(FolderName);

        Assert.Equal(3, lines.Count);
        Assert.Equal("First paragraph.", lines[0]);
        Assert.Equal("Second paragraph.", lines[1]);
        Assert.Equal("Third paragraph.", lines[2]);
    }

    [Fact]
    public async Task FlattenFromDb_NullOrWhitespaceItems_Excluded()
    {
        await SeedParagraphsAsync(
            ["  ", null!],
            ["Real content."]
        );

        var lines = await _sut.FlattenFromDbAsync(FolderName);

        Assert.Single(lines);
        Assert.Equal("Real content.", lines[0]);
    }

    // ── FlattenFromFileAsync (text file) ─────────────────────────────────────

    [Fact]
    public async Task FlattenFromFile_TextFile_ReturnsNonEmptyLines()
    {
        var textFile = Path.Combine(_tempDir, "book.txt");
        await File.WriteAllTextAsync(textFile, "Line one\n\nLine two\nLine three\n");

        await SeedDbProjectAsync("book.txt", BookFileType.Text);

        var lines = await _sut.FlattenFromFileAsync(FolderName);

        Assert.Equal(3, lines.Count);
        Assert.Equal("Line one", lines[0]);
        Assert.Equal("Line two", lines[1]);
        Assert.Equal("Line three", lines[2]);
    }

    [Fact]
    public async Task FlattenFromFile_TextFile_ChapterNumberLines_Preserved()
    {
        // Simulate the painted-man scenario: digit-only lines are chapter headers
        var textFile = Path.Combine(_tempDir, "book.txt");
        await File.WriteAllTextAsync(textFile, "1\nFirst chapter content.\n2\nSecond chapter content.\n");

        await SeedDbProjectAsync("book.txt", BookFileType.Text);

        var lines = await _sut.FlattenFromFileAsync(FolderName);

        Assert.Equal(4, lines.Count);
        Assert.Equal("1", lines[0]);
        Assert.Equal("2", lines[2]);
    }
}
