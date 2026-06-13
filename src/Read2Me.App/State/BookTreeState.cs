using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Read2Me.Data.Entities;
using Read2Me.Services;

namespace Read2Me.App.State
{
    public class BookTreeState
    {
        private readonly ProjectService _projectService;
        private readonly string _folderName;

        public Dictionary<Guid, List<Part>> LoadedParts { get; } = new();
        public Dictionary<Guid, List<Chapter>> LoadedChapters { get; } = new();
        public Dictionary<Guid, List<Paragraph>> LoadedParagraphs { get; } = new();
        public HashSet<Guid> LoadingIds { get; } = new();

        public BookTreeState(ProjectService projectService, string folderName)
        {
            _projectService = projectService;
            _folderName = folderName;
        }

        public void Reset()
        {
            LoadedParts.Clear();
            LoadedChapters.Clear();
            LoadedParagraphs.Clear();
            LoadingIds.Clear();
        }

        public async Task OnVolumeExpandedAsync(Volume volume, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                LoadingIds.Add(volume.Id);
                notifyChanged();
                var parts = await _projectService.GetPartsAsync(_folderName, volume.Id);
                LoadedParts[volume.Id] = parts;
                LoadingIds.Remove(volume.Id);
                notifyChanged();
            }
            else
            {
                if (LoadedParts.TryGetValue(volume.Id, out var parts))
                {
                    foreach (var part in parts)
                    {
                        if (LoadedChapters.TryGetValue(part.Id, out var chapters))
                        {
                            foreach (var ch in chapters)
                                LoadedParagraphs.Remove(ch.Id);
                            LoadedChapters.Remove(part.Id);
                        }
                    }
                }
                LoadedParts.Remove(volume.Id);
            }
        }

        public async Task OnPartExpandedAsync(Part part, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                LoadingIds.Add(part.Id);
                notifyChanged();
                var chapters = await _projectService.GetChaptersAsync(_folderName, part.Id);
                LoadedChapters[part.Id] = chapters;
                LoadingIds.Remove(part.Id);
                notifyChanged();
            }
            else
            {
                if (LoadedChapters.TryGetValue(part.Id, out var chapters))
                {
                    foreach (var ch in chapters)
                        LoadedParagraphs.Remove(ch.Id);
                    LoadedChapters.Remove(part.Id);
                }
            }
        }

        public async Task OnChapterExpandedAsync(Chapter chapter, bool expanded, Action notifyChanged)
        {
            if (expanded)
            {
                LoadingIds.Add(chapter.Id);
                notifyChanged();
                var paragraphs = await _projectService.GetChapterParagraphsAsync(_folderName, chapter.Id);
                LoadedParagraphs[chapter.Id] = paragraphs;
                LoadingIds.Remove(chapter.Id);
                notifyChanged();
            }
            else
            {
                LoadedParagraphs.Remove(chapter.Id);
            }
        }
    }
}
