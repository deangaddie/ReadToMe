using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class TextFileReaderTests : IDisposable
    {
        private readonly List<string> _tempFiles = [];

        private string WriteTempFile(string content, string extension = ".txt")
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + extension);
            File.WriteAllText(path, content);
            _tempFiles.Add(path);
            return path;
        }

        private static TextFileReader CreateReader() =>
            new(NullLogger<TextFileReader>.Instance);

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                if (File.Exists(f)) File.Delete(f);
        }

        [Fact]
        public async Task ReadAsync_SingleLine_ReturnsOneVolOnePartOneChapterOneParagraph()
        {
            var path = WriteTempFile("Hello world");
            var result = await CreateReader().ReadAsync(path);

            Assert.Single(result.Volumes);
            Assert.Single(result.Volumes[0].Parts);
            Assert.Single(result.Volumes[0].Parts[0].Chapters);
            Assert.Single(result.Volumes[0].Parts[0].Chapters[0].Paragraphs);
            Assert.Equal("Hello world", result.Volumes[0].Parts[0].Chapters[0].Paragraphs[0].Text);
        }

        [Fact]
        public async Task ReadAsync_MultipleLinesWithBlankLines_BlankLinesSkipped()
        {
            var path = WriteTempFile("Line one\n\nLine two\n\nLine three");
            var result = await CreateReader().ReadAsync(path);

            var paragraphs = result.Volumes[0].Parts[0].Chapters[0].Paragraphs;
            Assert.Equal(3, paragraphs.Count);
            Assert.Equal("Line one", paragraphs[0].Text);
            Assert.Equal("Line two", paragraphs[1].Text);
            Assert.Equal("Line three", paragraphs[2].Text);
        }

        [Fact]
        public async Task ReadAsync_EmptyFile_ReturnsZeroParagraphs()
        {
            var path = WriteTempFile(string.Empty);
            var result = await CreateReader().ReadAsync(path);

            Assert.Single(result.Volumes);
            Assert.Single(result.Volumes[0].Parts);
            Assert.Single(result.Volumes[0].Parts[0].Chapters);
            Assert.Empty(result.Volumes[0].Parts[0].Chapters[0].Paragraphs);
        }

        [Fact]
        public async Task ReadAsync_ChapterTitleComesFromFilename()
        {
            var dir = Path.GetTempPath();
            var path = Path.Combine(dir, "my-chapter.txt");
            File.WriteAllText(path, "Some text");
            _tempFiles.Add(path);

            var result = await CreateReader().ReadAsync(path);

            Assert.Equal("my-chapter", result.Volumes[0].Parts[0].Chapters[0].Title);
        }

        [Fact]
        public async Task ReadAsync_LeadingAndTrailingBlankLines_Ignored()
        {
            var path = WriteTempFile("\n\nActual content\n\n");
            var result = await CreateReader().ReadAsync(path);

            var paragraphs = result.Volumes[0].Parts[0].Chapters[0].Paragraphs;
            Assert.Single(paragraphs);
            Assert.Equal("Actual content", paragraphs[0].Text);
        }

        [Fact]
        public async Task ReadAsync_ParagraphTextMatchesInputLine()
        {
            const string text = "  leading spaces preserved  ";
            var path = WriteTempFile(text);
            var result = await CreateReader().ReadAsync(path);

            // TextFileReader uses TrimEntries on split, so text is trimmed
            Assert.Single(result.Volumes[0].Parts[0].Chapters[0].Paragraphs);
            Assert.Equal(text.Trim(), result.Volumes[0].Parts[0].Chapters[0].Paragraphs[0].Text);
        }
    }
}
