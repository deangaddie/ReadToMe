using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.UseCases;
using Read2Me.Tests.Infrastructure;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.UseCases
{
    /// <summary>
    /// The import producer end to end: read the source file, stage the cover it carries, and commit
    /// the whole replacement as one Book mutation (ADR 0007).
    /// <para>
    /// The cases worth holding onto are the ones where the commit does <em>not</em> happen. A missing
    /// file, a refusal, a cancellation and a no-change all leave the Book as it was — and each of
    /// them has to leave the project folder as it was too, because a cover written for an import that
    /// never landed is an artifact nothing points at.
    /// </para>
    /// </summary>
    public class BookUseCasesTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _scope;
        private readonly ProjectFolderId _folder;

        public BookUseCasesTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<Read2Me.Services.Books.EpubFileReader>();
            services.AddScoped<Read2Me.Services.Books.TextFileReader>();
            services.AddScoped<BookReadingService>();
            services.AddScoped<BookUseCases>();
            services.AddLogging();
            _root = services.BuildServiceProvider();
            _scope = _root.CreateAsyncScope();
            _folder = new ProjectFolderId(FolderName);
        }

        private BookUseCases Sut => _scope.ServiceProvider.GetRequiredService<BookUseCases>();

        // ── committed imports ────────────────────────────────────────────────

        [Fact]
        public async Task Import_ReadsTheSourceFileIntoTheBook()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: "Hello there.\n\nAnd again.");

            var outcome = await Sut.ImportAsync(_folder);

            Assert.IsType<BookImportOutcome.Replaced>(outcome);
            await using var db = await OpenDbAsync();
            Assert.NotEmpty(await db.Paragraphs.ToListAsync());
        }

        [Fact]
        public async Task Reread_ReplacesTheContentThatWasThere()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: "The new text.");
            await new BookHierarchyBuilder(OpenDbAsync)
                .AddVolume("v1", v => v.AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "The old text."))))
                .AddHierarchyAsync();

            var outcome = await Sut.ImportAsync(_folder, reread: true);

            Assert.IsType<BookImportOutcome.Replaced>(outcome);
            await using var db = await OpenDbAsync();
            var texts = await db.ParagraphItems.Select(i => i.Text).ToListAsync();
            Assert.DoesNotContain("The old text.", texts);
            Assert.Contains("The new text.", texts);
        }

        [Fact]
        public async Task ManualReread_ReSplitsTheSourceFileUnderTheChosenOptions()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: "One.\nTwo.\nThree.");

            var outcome = await Sut.ImportManuallyAsync(_folder, new ManualReadOptions(HasVolumes: false, HasParts: false, VolumeRule: null, PartRule: null, ChapterRule: new SectionSplitRule(SplitDetectionMode.Prefix, "Chapter")));

            Assert.IsType<BookImportOutcome.Replaced>(outcome);
            await using var db = await OpenDbAsync();
            Assert.NotEmpty(await db.Paragraphs.ToListAsync());
        }

        // ── expected failures, each distinguishable from a committed replacement ──

        [Fact]
        public async Task Import_WhenTheSourceFileIsGone_ReportsAMissingFileAndLeavesTheBookAlone()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: null);

            var outcome = await Sut.ImportAsync(_folder);

            var failed = Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.Equal(BookImportFailure.FileMissing, failed.Reason);
            await using var db = await OpenDbAsync();
            Assert.Empty(await db.Paragraphs.ToListAsync());
        }

        [Fact]
        public async Task Import_WithoutAProjectRecord_ReportsAnInvalidProject()
        {
            await using var _ = await OpenDbAsync();

            var outcome = await Sut.ImportAsync(_folder);

            var failed = Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.Equal(BookImportFailure.Invalid, failed.Reason);
            Assert.Contains("No project record found", failed.Message);
        }

        [Fact]
        public async Task Import_ThatChangesNothing_IsUnchangedRatherThanReplaced()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub, epub: BuildEpub("with-cover", includeCover: true));

            var outcome = await Sut.ImportAsync(
                _folder, reread: false,
                (_, _) => Task.FromResult<BookMutationOutcome>(new BookMutationOutcome.NoChange()));

            // No commit, no revision, no receipt: nothing for any open Book View to rebuild for — and
            // nothing naming the cover that was staged for it either.
            Assert.IsType<BookImportOutcome.Unchanged>(outcome);
            Assert.False(File.Exists(Path.Combine(FolderPath, "cover.jpg")));
        }

        [Fact]
        public async Task Import_WhenTheMutationIsRefused_ReportsAConflictAndCommitsNothing()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: "Hello there.");

            var outcome = await Sut.ImportAsync(_folder, reread: false, Refuse(BookMutationRejection.Conflict));

            var failed = Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.Equal(BookImportFailure.Conflict, failed.Reason);
            await using var db = await OpenDbAsync();
            Assert.Empty(await db.Paragraphs.ToListAsync());
        }

        [Fact]
        public async Task Import_WhenTheMutationIsCancelled_IsToldApartFromAFailure()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: "Hello there.");

            var outcome = await Sut.ImportAsync(_folder, reread: false, Refuse(BookMutationRejection.Cancelled));

            var failed = Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.Equal(BookImportFailure.Cancelled, failed.Reason);
        }

        [Fact]
        public async Task Import_WhenCancelledBeforeTheFileIsRead_ReportsCancellation()
        {
            await SeedProjectAsync("book.txt", BookFileType.Text, contents: "Hello there.");
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var outcome = await Sut.ImportAsync(_folder, reread: false, cancelled.Token);

            var failed = Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.Equal(BookImportFailure.Cancelled, failed.Reason);
        }

        // ── the cover image, staged before the mutation ──────────────────────

        [Fact]
        public async Task Import_EpubWithCover_NoPriorCover_WritesTheCoverAndNamesIt()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub, epub: BuildEpub("with-cover", includeCover: true));

            var outcome = await Sut.ImportAsync(_folder);

            Assert.IsType<BookImportOutcome.Replaced>(outcome);
            await using var db = await OpenDbAsync();
            var project = await db.Projects.AsNoTracking().SingleAsync();
            Assert.Equal("cover.jpg", project.CoverImage);
            Assert.True(File.Exists(Path.Combine(FolderPath, project.CoverImage!)));
        }

        [Fact]
        public async Task Import_EpubWithCover_ProjectAlreadyHasCover_DoesNotOverwrite()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub,
                epub: BuildEpub("with-cover", includeCover: true), coverImage: "my-custom-cover.jpg");

            await Sut.ImportAsync(_folder);

            await using var db = await OpenDbAsync();
            Assert.Equal("my-custom-cover.jpg", (await db.Projects.AsNoTracking().SingleAsync()).CoverImage);
            Assert.False(File.Exists(Path.Combine(FolderPath, "cover.jpg")));
        }

        [Fact]
        public async Task Import_EpubWithoutCover_LeavesTheCoverUnset()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub, epub: BuildEpub("no-cover", includeCover: false));

            Assert.IsType<BookImportOutcome.Replaced>(await Sut.ImportAsync(_folder));

            await using var db = await OpenDbAsync();
            Assert.Null((await db.Projects.AsNoTracking().SingleAsync()).CoverImage);
        }

        [Fact]
        public async Task Import_WhenTheMutationDoesNotCommit_RemovesTheStagedCover()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub, epub: BuildEpub("with-cover", includeCover: true));

            await Sut.ImportAsync(_folder, reread: false, Refuse(BookMutationRejection.Conflict));

            // The Book never named it, so nothing may be left behind claiming to be this book's cover.
            Assert.False(File.Exists(Path.Combine(FolderPath, "cover.jpg")));
            await using var db = await OpenDbAsync();
            Assert.Null((await db.Projects.AsNoTracking().SingleAsync()).CoverImage);
        }

        /// <summary>
        /// The case the whole staging order exists to protect: a commit that succeeds and then
        /// throws on its way back — a circuit reconciling a Book it has already navigated away from.
        /// The import cannot tell that apart from a commit that never happened, so it must not tidy
        /// away a cover the Book may now name.
        /// </summary>
        [Fact]
        public async Task Import_WhenTheCommitThrows_KeepsTheStagedCoverRatherThanRiskingTheBooksOwn()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub, epub: BuildEpub("with-cover", includeCover: true));

            var outcome = await Sut.ImportAsync(
                _folder, reread: false,
                (_, _) => throw new InvalidOperationException("committed, but this view moved on"));

            Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.True(File.Exists(Path.Combine(FolderPath, "cover.jpg")));
        }

        [Fact]
        public async Task Import_WhenTheCommitDeclinesTheCover_RemovesTheFileItStaged()
        {
            await SeedProjectAsync("book.epub", BookFileType.Epub, epub: BuildEpub("with-cover", includeCover: true));

            // A commit that changed content but did not take the cover — what the transaction's own
            // recheck reports when someone set a cover between the staging read and the commit.
            var outcome = await Sut.ImportAsync(_folder, reread: false, (mutation, _) =>
                Task.FromResult<BookMutationOutcome>(new BookMutationOutcome.Committed(
                    new BookMutationReceipt(mutation.FolderId, mutation.Name, Guid.NewGuid(), 1,
                        new BookMutationEffects
                        {
                            Scope = BookMutationScope.WholeProject,
                            Facets = BookFacets.Structure,
                        }))));

            Assert.IsType<BookImportOutcome.Replaced>(outcome);
            Assert.False(File.Exists(Path.Combine(FolderPath, "cover.jpg")));
        }

        [Fact]
        public async Task Import_OfAFileTypeNothingCanRead_IsInvalidRatherThanUnexpected()
        {
            await SeedProjectAsync("book.bin", (BookFileType)99, contents: "whatever");

            var outcome = await Sut.ImportAsync(_folder);

            var failed = Assert.IsType<BookImportOutcome.Failed>(outcome);
            Assert.Equal(BookImportFailure.Invalid, failed.Reason);
        }

        // ── harness ──────────────────────────────────────────────────────────

        /// <summary>A commit that always refuses, for the outcomes a real writer only reaches under contention.</summary>
        private static CommitBookMutation Refuse(BookMutationRejection reason) =>
            (_, _) => Task.FromResult<BookMutationOutcome>(
                new BookMutationOutcome.Rejected(reason, "refused for the test"));

        private async Task SeedProjectAsync(
            string filename, BookFileType type, string? contents = null, byte[]? epub = null, string? coverImage = null)
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Read2Me.Data.Entities.Project
            {
                Id = Guid.NewGuid(),
                Title = "Test",
                BookTitle = "Test Book",
                Author = "Author",
                Filename = filename,
                Type = type,
                CoverImage = coverImage,
            });
            await db.SaveChangesAsync();

            if (epub is not null)
                await File.WriteAllBytesAsync(Path.Combine(FolderPath, filename), epub);
            else if (contents is not null)
                await File.WriteAllTextAsync(Path.Combine(FolderPath, filename), contents);
        }

        public override async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        // Minimal valid epub ZIP, with or without a JPEG cover image embedded.
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
    }
}
