using System.Text;
using System.Text.RegularExpressions;
using Read2Me.Core.Models;
using Read2Me.Services.BookEdits;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Builds a compact chapter-outline summary of a book for LLM prompts. Two variants: the
    /// titles-only outline character discovery reads (<see cref="BuildAsync"/>) and the edit
    /// planner's, which adds Volume and Part heading lines and a paragraph sample under each chapter
    /// (<see cref="BuildForEditAsync"/>). Both are capped at <see cref="ChapterLimit"/> chapters so
    /// the prompt stays small on large books.
    /// </summary>
    public sealed class ChapterOutlineBuilder(IBookContentReader reader)
    {
        public const int ChapterLimit = 20;

        /// <summary>How many content paragraphs the edit outline samples per chapter (the Edit source depth cap).</summary>
        private const int SampleParagraphs = 5;

        /// <summary>A sampled value is cut at this many characters, then gets "…".</summary>
        private const int SampleValueLength = 80;

        public Task<string> BuildAsync(ProjectFolderId folderId, CancellationToken ct) =>
            BuildCoreAsync(folderId, forEdit: false, ct);

        public Task<string> BuildForEditAsync(ProjectFolderId folderId, CancellationToken ct) =>
            BuildCoreAsync(folderId, forEdit: true, ct);

        private async Task<string> BuildCoreAsync(ProjectFolderId folderId, bool forEdit, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var volumes = await reader.GetVolumesAsync(folderId);
            var totalParts = await reader.GetTotalPartCountAsync(folderId);
            var totalChapters = await reader.GetTotalChapterCountAsync(folderId);
            sb.AppendLine($"{volumes.Count} volume(s), {totalParts} part(s), {totalChapters} chapter(s).");

            var chapterN = 0;
            var volumeN = 0;
            var partN = 0;
            foreach (var volume in volumes)
            {
                ct.ThrowIfCancellationRequested();
                if (chapterN >= ChapterLimit) break;
                volumeN++;
                if (forEdit) sb.AppendLine($"Volume {volumeN}: {TitleOrUntitled(volume.Title)}");
                var parts = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Volume, volume.Id)).Parts ?? [];
                foreach (var part in parts)
                {
                    if (chapterN >= ChapterLimit) break;
                    partN++;
                    if (forEdit) sb.AppendLine($"  Part {partN}: {TitleOrUntitled(part.Title)}");
                    var chapters = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Part, part.Id)).Chapters ?? [];
                    foreach (var chapter in chapters)
                    {
                        chapterN++;
                        if (chapterN > ChapterLimit) break;
                        if (!forEdit)
                        {
                            sb.AppendLine($"Chapter {chapterN}: {chapter.Title ?? "(untitled)"}");
                            continue;
                        }
                        sb.AppendLine($"    Chapter {chapterN}: {TitleOrUntitled(chapter.Title)}");
                        await AppendSampleAsync(sb, folderId, chapter.Id);
                    }
                }
            }
            if (chapterN >= ChapterLimit && totalChapters > ChapterLimit)
                sb.AppendLine($"... and {totalChapters - ChapterLimit} more chapters.");
            return sb.ToString();
        }

        /// <summary>The chapter's first content paragraphs as the values a source reads, labelled with the
        /// names a plan uses (<c>p1</c>..<c>p5</c>), or <c>(no paragraphs)</c>.</summary>
        private async Task AppendSampleAsync(StringBuilder sb, ProjectFolderId folderId, Guid chapterId)
        {
            var paragraphs = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Chapter, chapterId)).Paragraphs ?? [];
            var values = paragraphs
                .Select(EditSourceReader.ParagraphValue)
                .OfType<string>()
                .Take(SampleParagraphs)
                .ToList();
            if (values.Count == 0)
            {
                sb.AppendLine("      (no paragraphs)");
                return;
            }
            for (var i = 0; i < values.Count; i++)
                sb.AppendLine($"      p{i + 1}: {Cut(values[i])}");
        }

        private static string TitleOrUntitled(string? title) =>
            string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        /// <summary>One line: runs of whitespace (a paragraph can hold newlines) become a single space.</summary>
        private static string Cut(string value)
        {
            value = Whitespace.Replace(value, " ");
            return value.Length <= SampleValueLength ? value : value[..SampleValueLength] + "…";
        }

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    }
}
