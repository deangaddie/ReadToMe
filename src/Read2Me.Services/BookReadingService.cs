using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using VersOne.Epub;

namespace Read2Me.Services
{
    public class BookReadingService(
        IFileSystem fs,
        IProjectDbContextFactory dbFactory,
        EpubFileReader epubReader,
        TextFileReader textReader,
        IBookContentPersister persister,
        ILogger<BookReadingService> logger)
    {
        public async Task ReadBookAsync(string folderName, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Starting book read for project '{Folder}'", folderName);

            var folderPath = fs.GetProjectFolderPath(folderName);
            await using var db = await dbFactory.CreateAsync(folderPath);

            var project = await db.Projects.SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"No project record found in '{folderName}'.");

            var bookFilePath = Path.Combine(folderPath, project.Filename);
            if (!fs.FileExists(bookFilePath))
                throw new FileNotFoundException($"Book file not found: {bookFilePath}");

            var content = project.Type switch
            {
                BookFileType.Epub => await epubReader.ReadAsync(bookFilePath, cancellationToken),
                BookFileType.Text => await textReader.ReadAsync(bookFilePath, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported file type: {project.Type}")
            };

            await persister.PersistAsync(db, content, cancellationToken);
            logger.LogInformation("Book read complete for project '{Folder}'", folderName);
        }

        public async Task<List<string>> FlattenFromFileAsync(string folderName, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Flattening book content from source file for '{Folder}'", folderName);

            var folderPath = fs.GetProjectFolderPath(folderName);
            await using var db = await dbFactory.CreateAsync(folderPath);

            var project = await db.Projects.SingleOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"No project record found in '{folderName}'.");

            var bookFilePath = Path.Combine(folderPath, project.Filename);
            if (!fs.FileExists(bookFilePath))
                throw new FileNotFoundException($"Book file not found: {bookFilePath}");

            List<string> texts;
            if (project.Type == BookFileType.Epub)
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

        public async Task<List<string>> FlattenFromDbAsync(string folderName, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Flattening existing book content from DB for '{Folder}'", folderName);

            var folderPath = fs.GetProjectFolderPath(folderName);
            await using var db = await dbFactory.CreateAsync(folderPath);

            // Pull ordered paragraphs across the full hierarchy, joining each paragraph's items back into one line
            var volumeIds = await db.Volumes.OrderBy(v => v.Order).Select(v => v.Id).ToListAsync(cancellationToken);
            var texts = new List<string>();

            foreach (var volumeId in volumeIds)
            {
                var partIds = await db.Parts.Where(p => p.VolumeId == volumeId).OrderBy(p => p.Order).Select(p => p.Id).ToListAsync(cancellationToken);
                foreach (var partId in partIds)
                {
                    var chapterIds = await db.Chapters.Where(c => c.PartId == partId).OrderBy(c => c.Order).Select(c => c.Id).ToListAsync(cancellationToken);
                    foreach (var chapterId in chapterIds)
                    {
                        var paraIds = await db.Paragraphs.Where(p => p.ChapterId == chapterId).OrderBy(p => p.Order).Select(p => p.Id).ToListAsync(cancellationToken);
                        foreach (var paraId in paraIds)
                        {
                            var items = await db.ParagraphItems
                                .Where(i => i.ParagraphId == paraId && i.Text != null)
                                .OrderBy(i => i.Order)
                                .Select(i => i.Text!)
                                .ToListAsync(cancellationToken);
                            var joined = string.Join(" ", items).Trim();
                            if (!string.IsNullOrWhiteSpace(joined))
                                texts.Add(joined);
                        }
                    }
                }
            }

            logger.LogInformation("Flattened {Count} paragraph(s) from DB for '{Folder}'", texts.Count, folderName);
            return texts;
        }

        public async Task ReadBookManuallyAsync(string folderName, List<string> lines, ManualReadOptions options, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Manual book read for project '{Folder}'", folderName);

            var folderPath = fs.GetProjectFolderPath(folderName);
            await using var db = await dbFactory.CreateAsync(folderPath);

            var content = ManualBookReader.Read(lines, options);

            await persister.PersistAsync(db, content, cancellationToken);
            logger.LogInformation("Manual book read complete for project '{Folder}'", folderName);
        }
    }
}
