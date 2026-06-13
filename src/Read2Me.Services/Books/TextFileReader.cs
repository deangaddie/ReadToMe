using System.IO;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;

namespace Read2Me.Services.Books
{
    public class TextFileReader(ILogger<TextFileReader> logger)
    {
        public async Task<BookContent> ReadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Reading text file: {Path}", filePath);

            var text = await File.ReadAllTextAsync(filePath, cancellationToken);
            var lines = text.Split('\n', StringSplitOptions.TrimEntries);

            var paragraphs = new List<ParagraphContent>();
            foreach (var line in lines)
            {
                if (!string.IsNullOrEmpty(line))
                    paragraphs.Add(new ParagraphContent(line));
            }

            var chapterTitle = Path.GetFileNameWithoutExtension(filePath);
            logger.LogInformation("Text file parsed: {Count} paragraph(s)", paragraphs.Count);

            return new BookContent([
                new VolumeContent("Volume 1", [
                    new PartContent(null, [
                        new ChapterContent(chapterTitle, paragraphs)
                    ])
                ])
            ]);
        }
    }
}
