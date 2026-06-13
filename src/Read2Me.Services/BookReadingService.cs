using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;

namespace Read2Me.Services
{
    public class BookReadingService(
        IOptions<WorkspaceOptions> options,
        EpubFileReader epubReader,
        TextFileReader textReader,
        ILogger<BookReadingService> logger)
    {
        private readonly WorkspaceOptions _workspace = options.Value;

        public async Task ReadBookAsync(string folderName, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Starting book read for project '{Folder}'", folderName);

            var folderPath = Path.Combine(_workspace.FolderPath, folderName);
            var dbPath = Path.Combine(folderPath, "project.db");

            var dbOptions = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=false")
                .Options;

            await using var db = new ProjectDbContext(dbOptions);
            await db.Database.MigrateAsync(cancellationToken);

            var project = await db.Projects.FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"No project record found in '{folderName}'.");

            var bookFilePath = Path.Combine(folderPath, project.Filename);
            if (!File.Exists(bookFilePath))
                throw new FileNotFoundException($"Book file not found: {bookFilePath}");

            var content = project.Type switch
            {
                BookFileType.Epub => await epubReader.ReadAsync(bookFilePath, cancellationToken),
                BookFileType.Text => await textReader.ReadAsync(bookFilePath, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported file type: {project.Type}")
            };

            await BookContentPersister.PersistAsync(db, content, cancellationToken);
            logger.LogInformation("Book read complete for project '{Folder}'", folderName);
        }

    }
}
