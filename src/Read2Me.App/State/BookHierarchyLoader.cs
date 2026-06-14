using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;

namespace Read2Me.App.State
{
    public class BookHierarchyLoader(IProjectReader reader)
    {
        private readonly Dictionary<ProjectFolderId, FolderCache> _caches = new();

        public FolderCache For(ProjectFolderId folderId)
        {
            if (!_caches.TryGetValue(folderId, out var cache))
                _caches[folderId] = cache = new FolderCache();
            return cache;
        }

        public async Task LoadPartsAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var parts = await reader.GetPartsAsync(folderId, volumeId);
            For(folderId).Parts[volumeId] = parts;
        }

        public async Task LoadChaptersAsync(ProjectFolderId folderId, Guid partId)
        {
            var chapters = await reader.GetChaptersAsync(folderId, partId);
            For(folderId).Chapters[partId] = chapters;
        }

        public async Task LoadParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var paragraphs = await reader.GetChapterParagraphsAsync(folderId, chapterId);
            For(folderId).Paragraphs[chapterId] = paragraphs;
        }

        public void Reset(ProjectFolderId folderId) => _caches.Remove(folderId);
    }

    public class FolderCache
    {
        public Dictionary<Guid, List<Part>> Parts { get; } = new();
        public Dictionary<Guid, List<Chapter>> Chapters { get; } = new();
        public Dictionary<Guid, List<Paragraph>> Paragraphs { get; } = new();
    }
}
