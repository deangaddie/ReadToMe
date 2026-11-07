using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using VersOne.Epub;

namespace Read2Me.Services
{
    /// <summary>What reading a project's source file produced, before anything is written.</summary>
    public sealed record BookReadResult(BookContent Content, byte[]? CoverImage);

    /// <summary>
    /// Reads a project's source file into Book content. It writes nothing: the content it returns is
    /// staged into the Book by the one import mutation that replaces it (ADR 0007), so the file read
    /// — the slow, failure-prone half — happens outside any transaction or write lock.
    /// </summary>
    public class BookReadingService(
        ProjectDbSession session,
        EpubFileReader epubReader,
        TextFileReader textReader,
        ILogger<BookReadingService> logger)
    {
        public async Task<BookReadResult> ReadBookAsync(string folderName, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Starting book read for project '{Folder}'", folderName);

            var (path, type) = await ResolveBookFileAsync(folderName, cancellationToken);

            // Only an epub carries a cover image; a text file is text.
            var read = type switch
            {
                BookFileType.Epub => ToResult(await epubReader.ReadAsync(path, cancellationToken)),
                BookFileType.Text => new BookReadResult(await textReader.ReadAsync(path, cancellationToken), null),
                _ => throw new NotSupportedException($"Unsupported file type: {type}")
            };

            logger.LogInformation("Book read complete for project '{Folder}'", folderName);
            return read;
        }

        public async Task<List<string>> FlattenFromFileAsync(string folderName, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Flattening book content from source file for '{Folder}'", folderName);

            var (bookFilePath, type) = await ResolveBookFileAsync(folderName, cancellationToken);

            List<string> texts;
            if (type == BookFileType.Epub)
            {
                var epub = await EpubReader.ReadBookAsync(bookFilePath);
                texts = epub.ReadingOrder
                    .SelectMany(f => EpubFileReader.ParseHtml(f.Content))
                    .Select(p => p.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();
            }
            else
            {
                var raw = await File.ReadAllTextAsync(bookFilePath, cancellationToken);
                texts = raw.Split('\n', StringSplitOptions.TrimEntries)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
            }

            logger.LogInformation("Flattened {Count} line(s) from source file for '{Folder}'", texts.Count, folderName);
            return texts;
        }

        private static BookReadResult ToResult(EpubReadResult epub) => new(epub.Content, epub.CoverImage);

        /// <summary>Re-splits already-flattened lines under hand-chosen options. Reads nothing and writes nothing.</summary>
        public BookContent ReadManually(List<string> lines, ManualReadOptions options) =>
            ManualBookReader.Read(lines, options);

        /// <summary>
        /// The project's source file, or the expected failure that says why there is not one.
        /// </summary>
        /// <exception cref="InvalidOperationException">The folder holds no project record.</exception>
        /// <exception cref="FileNotFoundException">The project names a file that is not there.</exception>
        private async Task<(string Path, BookFileType Type)> ResolveBookFileAsync(
            string folderName, CancellationToken cancellationToken)
        {
            var db = await session.OpenAsync(folderName);

            var project = await db.Projects.SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"No project record found in '{folderName}'.");

            var folderPath = session.FileSystem.GetProjectFolderPath(folderName);
            var bookFilePath = Path.Combine(folderPath, project.Filename);
            if (!session.FileSystem.FileExists(bookFilePath))
                throw new FileNotFoundException($"Book file not found: {bookFilePath}");

            return (bookFilePath, project.Type);
        }
    }
}
