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
        Task<Project?> GetProjectAsync(string folderName);
        Task<bool> HasBookContentAsync(string folderName);
        Task<List<Volume>> GetVolumesAsync(string folderName);
        Task<List<Part>> GetPartsAsync(string folderName, Guid volumeId);
        Task<List<Chapter>> GetChaptersAsync(string folderName, Guid partId);
        Task<List<Paragraph>> GetChapterParagraphsAsync(string folderName, Guid chapterId);
    }
}
