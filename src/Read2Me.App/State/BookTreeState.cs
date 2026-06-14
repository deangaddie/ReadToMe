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

        public HashSet<Guid> LoadingIds { get; } = new();

        public HashSet<Guid> ExpandedVolumeIds { get; } = new();
        public HashSet<Guid> ExpandedPartIds { get; } = new();
        public HashSet<Guid> ExpandedChapterIds { get; } = new();

        // Read-only views — callers cannot mutate the underlying dictionaries.
        public IReadOnlyList<Part>? GetParts(Guid volumeId) =>
            _loader.For(_folderId).Parts.TryGetValue(volumeId, out var v) ? v : null;

        public IReadOnlyList<Chapter>? GetChapters(Guid partId) =>
            _loader.For(_folderId).Chapters.TryGetValue(partId, out var v) ? v : null;

        public IReadOnlyList<Paragraph>? GetParagraphs(Guid chapterId) =>
            _loader.For(_folderId).Paragraphs.TryGetValue(chapterId, out var v) ? v : null;

        // Kept for backward-compat with BookTab rendering that uses TryGetValue directly.
        public Dictionary<Guid, List<Part>> LoadedParts => _loader.For(_folderId).Parts;
        public Dictionary<Guid, List<Chapter>> LoadedChapters => _loader.For(_folderId).Chapters;
        public Dictionary<Guid, List<Paragraph>> LoadedParagraphs => _loader.For(_folderId).Paragraphs;

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
            foreach (var list in _loader.For(_folderId).Paragraphs.Values)
                list.RemoveAll(p => p.Id == paragraphId);
        }

        // Collapse cascades: volume -> parts -> chapters -> paragraphs.
        public void CollapseVolume(Guid volumeId)
        {
            var cache = _loader.For(_folderId);
            if (!cache.Parts.TryGetValue(volumeId, out var parts)) return;
            foreach (var part in parts)
                CollapsePartInternal(cache, part.Id);
            cache.Parts.Remove(volumeId);
        }

        public void CollapsePart(Guid partId)
        {
            var cache = _loader.For(_folderId);
            CollapsePartInternal(cache, partId);
        }

        public void CollapseChapter(Guid chapterId)
        {
            _loader.For(_folderId).Paragraphs.Remove(chapterId);
        }

        private static void CollapsePartInternal(FolderCache cache, Guid partId)
        {
            if (!cache.Chapters.TryGetValue(partId, out var chapters)) return;
            foreach (var ch in chapters)
                cache.Paragraphs.Remove(ch.Id);
            cache.Chapters.Remove(partId);
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

                var parts = _loader.For(_folderId).Parts.TryGetValue(vid, out var p) ? p : null;
                if (parts == null) continue;

                // Auto-skip hidden single-part — treat it as expanded always
                var expandedParts = parts.Count == 1
                    ? parts.ToList()
                    : parts.Where(pt => ExpandedPartIds.Contains(pt.Id)).ToList();

                foreach (var part in expandedParts)
                {
                    await _loader.LoadChaptersAsync(_folderId, part.Id);
                    ExpandedPartIds.Add(part.Id);

                    var chapters = _loader.For(_folderId).Chapters.TryGetValue(part.Id, out var ch) ? ch : null;
                    if (chapters == null) continue;

                    foreach (var chapter in chapters.Where(c => ExpandedChapterIds.Contains(c.Id)))
                        await _loader.LoadParagraphsAsync(_folderId, chapter.Id);
                }
            }
        }

        public async Task OnVolumeExpandedAsync(Volume volume, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                ExpandedVolumeIds.Add(volume.Id);
                LoadingIds.Add(volume.Id);
                notifyChanged();
                await _loader.LoadPartsAsync(_folderId, volume.Id);

                // Auto-expand single part
                if (_loader.For(_folderId).Parts.TryGetValue(volume.Id, out var parts) && parts.Count == 1)
                {
                    ExpandedPartIds.Add(parts[0].Id);
                    LoadingIds.Add(parts[0].Id);
                    notifyChanged();
                    await _loader.LoadChaptersAsync(_folderId, parts[0].Id);
                    LoadingIds.Remove(parts[0].Id);
                }

                LoadingIds.Remove(volume.Id);
                notifyChanged();
            }
            else
            {
                ExpandedVolumeIds.Remove(volume.Id);
                CollapseVolume(volume.Id);
            }
        }

        public async Task OnPartExpandedAsync(Part part, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                ExpandedPartIds.Add(part.Id);
                LoadingIds.Add(part.Id);
                notifyChanged();
                await _loader.LoadChaptersAsync(_folderId, part.Id);
                LoadingIds.Remove(part.Id);
                notifyChanged();
            }
            else
            {
                ExpandedPartIds.Remove(part.Id);
                CollapsePart(part.Id);
            }
        }

        public async Task OnChapterExpandedAsync(Chapter chapter, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                ExpandedChapterIds.Add(chapter.Id);
                LoadingIds.Add(chapter.Id);
                notifyChanged();
                await _loader.LoadParagraphsAsync(_folderId, chapter.Id);
                LoadingIds.Remove(chapter.Id);
                notifyChanged();
            }
            else
            {
                ExpandedChapterIds.Remove(chapter.Id);
                CollapseChapter(chapter.Id);
            }
        }
    }
}
