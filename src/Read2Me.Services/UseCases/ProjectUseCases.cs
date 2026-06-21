using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;

namespace Read2Me.Services.UseCases
{
    public class ProjectUseCases(IProjectReader reader, IProjectWriter writer)
    {
        public async Task<Result<string>> CreateAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType)
        {
            try
            {
                var folderName = await writer.CreateProjectAsync(title, bookTitle, author, originalFileName, fileStream, fileType);
                return Result<string>.Ok(folderName);
            }
            catch (ArgumentException ex) { return Result<string>.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result<string>.Fail(ex.Message); }
            catch (IOException) { return Result<string>.Fail("Failed to save book file. Please try again."); }
            catch (Exception) { return Result<string>.Fail("Failed to create project. Please try again."); }
        }

        public async Task<Result<IReadOnlyList<ProjectSummary>>> GetSummariesAsync()
        {
            try
            {
                var summaries = await reader.GetProjectSummariesAsync();
                return Result<IReadOnlyList<ProjectSummary>>.Ok(summaries);
            }
            catch (Exception) { return Result<IReadOnlyList<ProjectSummary>>.Fail("Failed to load projects."); }
        }

        public Result DeleteProject(string folderName)
        {
            try
            {
                writer.DeleteProject(folderName);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to delete project. Please try again."); }
        }

        public async Task<Result> SaveCoverImageAsync(string folderName, string filename, Stream stream)
        {
            try
            {
                await writer.SaveCoverImageAsync(folderName, filename, stream);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to save cover image."); }
        }

        public async Task<Result> DeleteCoverImageAsync(string folderName)
        {
            try
            {
                await writer.DeleteCoverImageAsync(folderName);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to delete cover image."); }
        }

        public async Task<Result> SetNarratorOnlyModeAsync(string folderName, bool value)
        {
            try
            {
                await writer.SetNarratorOnlyModeAsync(new ProjectFolderId(folderName), value);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to save narrator-only setting."); }
        }
    }
}
