using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Read2Me.Data.Entities;
using Read2Me.Services;

namespace Read2Me.App.State
{
    public class BookHierarchyLoader(IProjectReader reader)
    {
        private readonly Dictionary<string, FolderCache> _caches = new(StringComparer.OrdinalIgnoreCase);

        public FolderCache For(string folderName)
        {
            if (!_caches.TryGetValue(folderName, out var cache))
                _caches[folderName] = cache = new FolderCache();
            return cache;
        }

        public async Task LoadPartsAsync(string folderName, Guid volumeId)
        {
            var parts = await reader.GetPartsAsync(folderName, volumeId);
            For(folderName).Parts[volumeId] = parts;
        }

        public async Task LoadChaptersAsync(string folderName, Guid partId)
        {
            var chapters = await reader.GetChaptersAsync(folderName, partId);
            For(folderName).Chapters[partId] = chapters;
        }

        public async Task LoadParagraphsAsync(string folderName, Guid chapterId)
        {
            var paragraphs = await reader.GetChapterParagraphsAsync(folderName, chapterId);
            For(folderName).Paragraphs[chapterId] = paragraphs;
        }

        public void Reset(string folderName) => _caches.Remove(folderName);
    }

    public class FolderCache
    {
        public Dictionary<Guid, List<Part>> Parts { get; } = new();
        public Dictionary<Guid, List<Chapter>> Chapters { get; } = new();
        public Dictionary<Guid, List<Paragraph>> Paragraphs { get; } = new();
    }
}
