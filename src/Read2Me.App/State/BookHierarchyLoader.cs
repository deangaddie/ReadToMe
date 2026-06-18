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
            var children = await reader.GetChildrenAsync(folderId, BookNodeLevel.Volume, volumeId);
            For(folderId).SetParts(volumeId, children.Parts!);
        }

        public async Task LoadChaptersAsync(ProjectFolderId folderId, Guid partId)
        {
            var children = await reader.GetChildrenAsync(folderId, BookNodeLevel.Part, partId);
            For(folderId).SetChapters(partId, children.Chapters!);
        }

        public async Task LoadParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var children = await reader.GetChildrenAsync(folderId, BookNodeLevel.Chapter, chapterId);
            For(folderId).SetParagraphs(chapterId, children.Paragraphs!);
        }

        public void Reset(ProjectFolderId folderId) => _caches.Remove(folderId);
    }

    public class FolderCache
    {
        private readonly Dictionary<Guid, List<Part>> _parts = new();
        private readonly Dictionary<Guid, List<Chapter>> _chapters = new();
        private readonly Dictionary<Guid, List<Paragraph>> _paragraphs = new();

        public List<Part>? GetParts(Guid volumeId) => _parts.GetValueOrDefault(volumeId);
        public List<Chapter>? GetChapters(Guid partId) => _chapters.GetValueOrDefault(partId);
        public List<Paragraph>? GetParagraphs(Guid chapterId) => _paragraphs.GetValueOrDefault(chapterId);

        public void SetParts(Guid volumeId, List<Part> parts) => _parts[volumeId] = parts;
        public void SetChapters(Guid partId, List<Chapter> chapters) => _chapters[partId] = chapters;
        public void SetParagraphs(Guid chapterId, List<Paragraph> paragraphs) => _paragraphs[chapterId] = paragraphs;

        public void RemoveVolume(Guid volumeId) => _parts.Remove(volumeId);
        public void RemovePart(Guid partId) => _chapters.Remove(partId);
        public void RemoveChapter(Guid chapterId) => _paragraphs.Remove(chapterId);

        public bool HasParts(Guid volumeId) => _parts.ContainsKey(volumeId);

        public void RemoveParagraphEverywhere(Guid paragraphId)
        {
            foreach (var list in _paragraphs.Values)
                list.RemoveAll(p => p.Id == paragraphId);
        }
    }
}
