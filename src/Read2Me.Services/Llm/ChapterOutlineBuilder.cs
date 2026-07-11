using System.Text;
using Read2Me.Core.Models;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// Builds a compact chapter-outline summary of a book for LLM prompts (book-edit
    /// planning and character discovery). Capped at <see cref="ChapterLimit"/> chapters
    /// so the prompt stays small on large books.
    /// </summary>
    public sealed class ChapterOutlineBuilder(IBookContentReader reader)
    {
        public const int ChapterLimit = 20;

        public async Task<string> BuildAsync(ProjectFolderId folderId, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var volumes = await reader.GetVolumesAsync(folderId);
            var totalParts = await reader.GetTotalPartCountAsync(folderId);
            var totalChapters = await reader.GetTotalChapterCountAsync(folderId);
            sb.AppendLine($"{volumes.Count} volume(s), {totalParts} part(s), {totalChapters} chapter(s).");

            var chapterN = 0;
            foreach (var volume in volumes)
            {
                ct.ThrowIfCancellationRequested();
                if (chapterN >= ChapterLimit) break;
                var parts = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Volume, volume.Id)).Parts ?? [];
                foreach (var part in parts)
                {
                    if (chapterN >= ChapterLimit) break;
                    var chapters = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Part, part.Id)).Chapters ?? [];
                    foreach (var chapter in chapters)
                    {
                        chapterN++;
                        if (chapterN > ChapterLimit) break;
                        sb.AppendLine($"Chapter {chapterN}: {chapter.Title ?? "(untitled)"}");
                    }
                }
            }
            if (chapterN >= ChapterLimit && totalChapters > ChapterLimit)
                sb.AppendLine($"... and {totalChapters - ChapterLimit} more chapters.");
            return sb.ToString();
        }
    }
}
