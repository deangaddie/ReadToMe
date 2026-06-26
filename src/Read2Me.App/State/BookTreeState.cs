using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.App.State
{
    public class BookTreeState(BookHierarchyLoader loader)
    {
        private readonly Dictionary<ProjectFolderId, PerFolderState> _states = new();

        public PerFolderState For(ProjectFolderId folderId)
        {
            if (!_states.TryGetValue(folderId, out var state))
                _states[folderId] = state = new PerFolderState(loader, folderId);
            return state;
        }
    }

    public class PerFolderState
    {
        private readonly BookHierarchyLoader _loader;
        private readonly ProjectFolderId _folderId;

        public event Action? Changed;
        private void NotifyChanged() => Changed?.Invoke();

        public HashSet<Guid> LoadingIds { get; } = new();

        public HashSet<Guid> ExpandedVolumeIds { get; } = new();
        public HashSet<Guid> ExpandedPartIds { get; } = new();
        public HashSet<Guid> ExpandedChapterIds { get; } = new();

        public List<Part>? GetParts(Guid volumeId) => _loader.For(_folderId).GetParts(volumeId);
        public List<Chapter>? GetChapters(Guid partId) => _loader.For(_folderId).GetChapters(partId);
        public List<Paragraph>? GetParagraphs(Guid chapterId) => _loader.For(_folderId).GetParagraphs(chapterId);
        public IEnumerable<Paragraph> AllParagraphs() => _loader.For(_folderId).AllParagraphs();
        public Paragraph? TryGetOwner(Guid itemId) => _loader.For(_folderId).TryGetOwner(itemId);

        public PerFolderState(BookHierarchyLoader loader, ProjectFolderId folderId)
        {
            _loader = loader;
            _folderId = folderId;
        }

        public void Reset()
        {
            _loader.Reset(_folderId);
            LoadingIds.Clear();
            // Preserve ExpandedVolumeIds / ExpandedPartIds / ExpandedChapterIds across reset
            // so RestoreExpandedAsync can reload them after data is refreshed.
        }

        // Transfer expansion from deleted ID to survivor ID after a merge.
        public void FixMergeExpansion(Guid survivorId, Guid deletedId)
        {
            if (ExpandedVolumeIds.Remove(deletedId)) ExpandedVolumeIds.Add(survivorId);
            if (ExpandedPartIds.Remove(deletedId)) ExpandedPartIds.Add(survivorId);
            if (ExpandedChapterIds.Remove(deletedId)) ExpandedChapterIds.Add(survivorId);
        }

        // After a split: if the split source panel was expanded, expand the
        // newly created sibling panel too (both halves stay open).
        public void MarkSplitExpansion(HashSet<Guid> expandedIds, Guid sourceId, Guid newId)
        {
            if (expandedIds.Contains(sourceId))
                expandedIds.Add(newId);
        }

        // Remove a paragraph from whichever chapter list contains it.
        public void RemoveParagraph(Guid paragraphId)
        {
            _loader.For(_folderId).RemoveParagraphEverywhere(paragraphId);
        }

        // Collapse cascades: volume -> parts -> chapters -> paragraphs.
        public void CollapseVolume(Guid volumeId)
        {
            var cache = _loader.For(_folderId);
            var parts = cache.GetParts(volumeId);
            if (parts == null) return;
            foreach (var part in parts)
                CollapsePartInternal(cache, part.Id);
            cache.RemoveVolume(volumeId);
        }

        public void CollapsePart(Guid partId)
        {
            CollapsePartInternal(_loader.For(_folderId), partId);
        }

        public void CollapseChapter(Guid chapterId)
        {
            _loader.For(_folderId).RemoveChapter(chapterId);
        }

        private static void CollapsePartInternal(FolderCache cache, Guid partId)
        {
            var chapters = cache.GetChapters(partId);
            if (chapters == null) return;
            foreach (var ch in chapters)
                cache.RemoveChapter(ch.Id);
            cache.RemovePart(partId);
        }

        // Reload data for all tracked expanded IDs after a reset.
        // Volumes -> Parts -> Chapters -> Paragraphs, respecting hierarchy.
        public async Task RestoreExpandedAsync()
        {
            // Reload parts for expanded volumes
            var volumeIds = ExpandedVolumeIds.ToList();
            foreach (var vid in volumeIds)
            {
                await _loader.LoadPartsAsync(_folderId, vid);

                var parts = _loader.For(_folderId).GetParts(vid);
                if (parts == null) continue;

                // Auto-skip hidden single-part — treat it as expanded always
                var expandedParts = parts.Count == 1
                    ? parts.ToList()
                    : parts.Where(pt => ExpandedPartIds.Contains(pt.Id)).ToList();

                foreach (var part in expandedParts)
                {
                    await _loader.LoadChaptersAsync(_folderId, part.Id);
                    ExpandedPartIds.Add(part.Id);

                    var chapters = _loader.For(_folderId).GetChapters(part.Id);
                    if (chapters == null) continue;

                    foreach (var chapter in chapters.Where(c => ExpandedChapterIds.Contains(c.Id)))
                        await _loader.LoadParagraphsAsync(_folderId, chapter.Id);
                }
            }
        }

        public async Task OnVolumeExpandedAsync(Volume volume, bool expanded)
        {
            if (expanded)
            {
                ExpandedVolumeIds.Add(volume.Id);
                LoadingIds.Add(volume.Id);
                NotifyChanged();
                await _loader.LoadPartsAsync(_folderId, volume.Id);

                // Auto-expand single part
                var parts = _loader.For(_folderId).GetParts(volume.Id);
                if (parts != null && parts.Count == 1)
                {
                    ExpandedPartIds.Add(parts[0].Id);
                    LoadingIds.Add(parts[0].Id);
                    NotifyChanged();
                    await _loader.LoadChaptersAsync(_folderId, parts[0].Id);
                    LoadingIds.Remove(parts[0].Id);
                }

                LoadingIds.Remove(volume.Id);
                NotifyChanged();
            }
            else
            {
                ExpandedVolumeIds.Remove(volume.Id);
                CollapseVolume(volume.Id);
            }
        }

        public async Task OnPartExpandedAsync(Part part, bool expanded)
        {
            if (expanded)
            {
                ExpandedPartIds.Add(part.Id);
                LoadingIds.Add(part.Id);
                NotifyChanged();
                await _loader.LoadChaptersAsync(_folderId, part.Id);
                LoadingIds.Remove(part.Id);
                NotifyChanged();
            }
            else
            {
                ExpandedPartIds.Remove(part.Id);
                CollapsePart(part.Id);
            }
        }

        public async Task OnChapterExpandedAsync(Chapter chapter, bool expanded)
        {
            if (expanded)
            {
                ExpandedChapterIds.Add(chapter.Id);
                LoadingIds.Add(chapter.Id);
                NotifyChanged();
                await _loader.LoadParagraphsAsync(_folderId, chapter.Id);
                LoadingIds.Remove(chapter.Id);
                NotifyChanged();
            }
            else
            {
                ExpandedChapterIds.Remove(chapter.Id);
                CollapseChapter(chapter.Id);
            }
        }
    }
}
