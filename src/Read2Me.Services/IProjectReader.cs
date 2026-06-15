using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.Services
{
    public interface IProjectReader
    {
        IReadOnlyList<string> GetProjects();
        Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync();
        Task<Project?> GetProjectAsync(ProjectFolderId folderId);
        Task<bool> HasBookContentAsync(ProjectFolderId folderId);
        Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId);
        Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId);
        Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId);
        Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId);
        Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId);
        Task<int> GetTotalPartCountAsync(ProjectFolderId folderId);
        Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId);

        Task<List<CharacterParagraphRef>> GetVolumeCharacterParagraphsAsync(ProjectFolderId folderId, Guid volumeId);
        Task<List<CharacterParagraphRef>> GetPartCharacterParagraphsAsync(ProjectFolderId folderId, Guid partId);
        Task<List<CharacterParagraphRef>> GetChapterCharacterParagraphsAsync(ProjectFolderId folderId, Guid chapterId);
        Task<int> GetVolumeCharacterParagraphCountAsync(ProjectFolderId folderId, Guid volumeId);
        Task<int> GetPartCharacterParagraphCountAsync(ProjectFolderId folderId, Guid partId);
        Task<int> GetChapterCharacterParagraphCountAsync(ProjectFolderId folderId, Guid chapterId);

        // All volume/part/chapter node ids that contain at least one character paragraph.
        Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId);
    }
}
