using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Books;
using Read2Me.Services.IO;
using Read2Me.Services.UseCases;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.UseCases
{
    public class BookUseCasesTests : ProjectDbTestBase
    {
        private (BookUseCases sut, IBookCommandHandler commandHandler, ProjectService writer) Build()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            var epubReader = new EpubFileReader(NullLogger<EpubFileReader>.Instance);
            var textReader = new TextFileReader(NullLogger<TextFileReader>.Instance);
            var persister = Substitute.For<IBookContentPersister>();
            var readingService = new BookReadingService(session, epubReader, textReader, persister, NullLogger<BookReadingService>.Instance);
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var writer = new ProjectService(fs, session, NullLogger<ProjectService>.Instance);
            var sut = new BookUseCases(readingService, commandHandler, session, writer);
            return (sut, commandHandler, writer);
        }

        // Minimal valid epub ZIP with a JPEG cover image embedded.
        // Returns the epub bytes and the JPEG bytes used as the cover.
        private static (byte[] epubBytes, byte[] jpegBytes) BuildEpubWithJpegCover()
        {
            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
            return (BuildEpub("test-epub", includeCover: true), jpeg);
        }

        private static byte[] BuildEpubWithoutCover() => BuildEpub("test-epub-no-cover", includeCover: false);

        private static byte[] BuildEpub(string uid, bool includeCover)
        {
            var container = "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>";

            var coverItem = includeCover
                ? "<item id=\"cover-image\" href=\"cover.jpg\" media-type=\"image/jpeg\" properties=\"cover-image\"/>"
                : "";
            var coverMeta = includeCover ? "<meta name=\"cover\" content=\"cover-image\"/>" : "";
            // epub3 requires a NAV document (properties="nav") in addition to NCX
            var opf = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"uid\">{uid}</dc:identifier><dc:title>Test Book</dc:title><dc:language>en</dc:language>{coverMeta}</metadata><manifest>{coverItem}<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/><item id=\"chapter1\" href=\"chapter1.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"chapter1\"/></spine></package>";

            var xhtml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Chapter 1</title></head><body><p>Hello world.</p></body></html>";

            var nav = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Nav</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"chapter1.xhtml\">Chapter 1</a></li></ol></nav></body></html>";

            var ncx = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head><meta name=\"dtb:uid\" content=\"{uid}\"/></head><docTitle><text>Test Book</text></docTitle><navMap><navPoint id=\"ch1\" playOrder=\"1\"><navLabel><text>Chapter 1</text></navLabel><content src=\"chapter1.xhtml\"/></navPoint></navMap></ncx>";

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipEntry(zip, "mimetype", "application/epub+zip", compress: false);
                WriteZipEntry(zip, "META-INF/container.xml", container);
                WriteZipEntry(zip, "OEBPS/content.opf", opf);
                WriteZipEntry(zip, "OEBPS/nav.xhtml", nav);
                WriteZipEntry(zip, "OEBPS/chapter1.xhtml", xhtml);
                WriteZipEntry(zip, "OEBPS/toc.ncx", ncx);

                if (includeCover)
                {
                    byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
                    var coverEntry = zip.CreateEntry("OEBPS/cover.jpg", CompressionLevel.NoCompression);
                    using var coverStream = coverEntry.Open();
                    coverStream.Write(jpeg);
                }
            }
            return ms.ToArray();
        }

        private static void WriteZipEntry(ZipArchive zip, string path, string content, bool compress = true)
        {
            var entry = zip.CreateEntry(path, compress ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        [Fact]
        public async Task ImportAsync_WithReread_IssuesClearCommandFirst()
        {
            var (sut, commandHandler, _) = Build();
            // No project record in db -> ReadBookAsync throws InvalidOperationException
            // which is still caught; what we care about is Clear was called first.
            await sut.ImportAsync(FolderName, reread: true);

            await commandHandler.Received(1).ExecuteAsync(
                Arg.Any<ClearBookContentCommand>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ImportAsync_WithoutReread_DoesNotIssueClearCommand()
        {
            var (sut, commandHandler, _) = Build();
            await sut.ImportAsync(FolderName, reread: false);

            await commandHandler.DidNotReceive().ExecuteAsync(
                Arg.Any<ClearBookContentCommand>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ImportAsync_WhenNoProjectRecord_ReturnsFailure()
        {
            // Seed only the DB schema (no Project row) so ReadBookAsync throws InvalidOperationException.
            await using var _ = await OpenDbAsync();

            var (sut, _, _) = Build();
            var result = await sut.ImportAsync(FolderName);

            Assert.False(result.IsSuccess);
            Assert.Contains("No project record found", result.Error);
        }

        [Fact]
        public async Task ImportAsync_WhenCommandHandlerThrows_ReturnsFailure()
        {
            var (sut, commandHandler, _) = Build();
            commandHandler.ExecuteAsync(Arg.Any<ClearBookContentCommand>(), Arg.Any<CancellationToken>())
                .ThrowsForAnyArgs(new Exception("db locked"));

            var result = await sut.ImportAsync(FolderName, reread: true);

            Assert.False(result.IsSuccess);
        }

        // ── Cover image extraction ────────────────────────────────────────────

        [Fact]
        public async Task ImportAsync_EpubWithCover_NoPriorCover_SavesCoverToProjectFolder()
        {
            var (epubBytes, _) = BuildEpubWithJpegCover();
            await using var db = await OpenDbAsync();
            await db.Projects.AddAsync(new Read2Me.Data.Entities.Project
            {
                Title = "Test", BookTitle = "Test Book", Author = "Author",
                Filename = "book.epub", Type = BookFileType.Epub,
            });
            await db.SaveChangesAsync();
            File.WriteAllBytes(Path.Combine(FolderPath, "book.epub"), epubBytes);

            var (sut, _, _) = Build();
            var result = await sut.ImportAsync(FolderName);

            Assert.True(result.IsSuccess, result.Error);
            // Re-query with no tracking to bypass EF change-tracker cache
            var project = await db.Projects.AsNoTracking().SingleAsync();
            Assert.NotNull(project.CoverImage);
            Assert.True(File.Exists(Path.Combine(FolderPath, project.CoverImage)));
            Assert.Equal(".jpg", Path.GetExtension(project.CoverImage));
        }

        [Fact]
        public async Task ImportAsync_EpubWithCover_ProjectAlreadyHasCover_DoesNotOverwrite()
        {
            var (epubBytes, _) = BuildEpubWithJpegCover();
            await using var db = await OpenDbAsync();
            await db.Projects.AddAsync(new Read2Me.Data.Entities.Project
            {
                Title = "Test", BookTitle = "Test Book", Author = "Author",
                Filename = "book.epub", Type = BookFileType.Epub,
                CoverImage = "my-custom-cover.jpg",
            });
            await db.SaveChangesAsync();
            File.WriteAllBytes(Path.Combine(FolderPath, "book.epub"), epubBytes);

            var (sut, _, _) = Build();
            var result = await sut.ImportAsync(FolderName);

            Assert.True(result.IsSuccess, result.Error);
            var project = await db.Projects.AsNoTracking().SingleAsync();
            Assert.Equal("my-custom-cover.jpg", project.CoverImage);
        }

        [Fact]
        public async Task ImportAsync_EpubWithoutCover_ImportSucceeds_CoverImageRemainsNull()
        {
            var epubBytes = BuildEpubWithoutCover();
            await using var db = await OpenDbAsync();
            await db.Projects.AddAsync(new Read2Me.Data.Entities.Project
            {
                Title = "Test", BookTitle = "Test Book", Author = "Author",
                Filename = "book.epub", Type = BookFileType.Epub,
            });
            await db.SaveChangesAsync();
            File.WriteAllBytes(Path.Combine(FolderPath, "book.epub"), epubBytes);

            var (sut, _, _) = Build();
            var result = await sut.ImportAsync(FolderName);

            Assert.True(result.IsSuccess, result.Error);
            var project = await db.Projects.AsNoTracking().SingleAsync();
            Assert.Null(project.CoverImage);
        }

        [Fact]
        public async Task ImportAsync_CoverSaveThrows_ImportStillReturnsOk()
        {
            var (epubBytes, _) = BuildEpubWithJpegCover();
            await using var db = await OpenDbAsync();
            await db.Projects.AddAsync(new Read2Me.Data.Entities.Project
            {
                Title = "Test", BookTitle = "Test Book", Author = "Author",
                Filename = "book.epub", Type = BookFileType.Epub,
            });
            await db.SaveChangesAsync();
            File.WriteAllBytes(Path.Combine(FolderPath, "book.epub"), epubBytes);

            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            var epubReader = new EpubFileReader(NullLogger<EpubFileReader>.Instance);
            var textReader = new TextFileReader(NullLogger<TextFileReader>.Instance);
            var persister = Substitute.For<IBookContentPersister>();
            var readingService = new BookReadingService(session, epubReader, textReader, persister, NullLogger<BookReadingService>.Instance);
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var failingWriter = Substitute.For<IProjectWriter>();
            failingWriter.SaveCoverImageAsync(Arg.Any<ProjectFolderId>(), Arg.Any<string>(), Arg.Any<Stream>())
                .ThrowsForAnyArgs(new IOException("disk full"));
            var sut = new BookUseCases(readingService, commandHandler, session, failingWriter);

            var result = await sut.ImportAsync(FolderName);

            Assert.True(result.IsSuccess, result.Error);
        }
    }
}
