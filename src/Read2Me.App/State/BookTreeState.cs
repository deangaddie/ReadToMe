using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Read2Me.Data.Entities;

namespace Read2Me.App.State
{
    public class BookTreeState(BookHierarchyLoader loader)
    {
        private readonly Dictionary<string, PerFolderState> _states = new(StringComparer.OrdinalIgnoreCase);

        public PerFolderState For(string folderName)
        {
            if (!_states.TryGetValue(folderName, out var state))
                _states[folderName] = state = new PerFolderState(loader, folderName);
            return state;
        }
    }

    public class PerFolderState
    {
        private readonly BookHierarchyLoader _loader;
        private readonly string _folderName;

        public HashSet<Guid> LoadingIds { get; } = new();

        public Dictionary<Guid, List<Part>> LoadedParts => _loader.For(_folderName).Parts;
        public Dictionary<Guid, List<Chapter>> LoadedChapters => _loader.For(_folderName).Chapters;
        public Dictionary<Guid, List<Paragraph>> LoadedParagraphs => _loader.For(_folderName).Paragraphs;

        public PerFolderState(BookHierarchyLoader loader, string folderName)
        {
            _loader = loader;
            _folderName = folderName;
        }

        public void Reset()
        {
            _loader.Reset(_folderName);
            LoadingIds.Clear();
        }

        public async Task OnVolumeExpandedAsync(Volume volume, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                LoadingIds.Add(volume.Id);
                notifyChanged();
                await _loader.LoadPartsAsync(_folderName, volume.Id);
                LoadingIds.Remove(volume.Id);
                notifyChanged();
            }
            else
            {
                var cache = _loader.For(_folderName);
                if (cache.Parts.TryGetValue(volume.Id, out var parts))
                {
                    foreach (var part in parts)
                    {
                        if (cache.Chapters.TryGetValue(part.Id, out var chapters))
                        {
                            foreach (var ch in chapters)
                                cache.Paragraphs.Remove(ch.Id);
                            cache.Chapters.Remove(part.Id);
                        }
                    }
                    cache.Parts.Remove(volume.Id);
                }
            }
        }

        public async Task OnPartExpandedAsync(Part part, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                LoadingIds.Add(part.Id);
                notifyChanged();
                await _loader.LoadChaptersAsync(_folderName, part.Id);
                LoadingIds.Remove(part.Id);
                notifyChanged();
            }
            else
            {
                var cache = _loader.For(_folderName);
                if (cache.Chapters.TryGetValue(part.Id, out var chapters))
                {
                    foreach (var ch in chapters)
                        cache.Paragraphs.Remove(ch.Id);
                    cache.Chapters.Remove(part.Id);
                }
            }
        }

        public async Task OnChapterExpandedAsync(Chapter chapter, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                LoadingIds.Add(chapter.Id);
                notifyChanged();
                await _loader.LoadParagraphsAsync(_folderName, chapter.Id);
                LoadingIds.Remove(chapter.Id);
                notifyChanged();
            }
            else
            {
                _loader.For(_folderName).Paragraphs.Remove(chapter.Id);
            }
        }
    }
}
