using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;

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

            var project = await db.Projects.FirstOrDefaultAsync(cancellationToken)
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
    }
}
