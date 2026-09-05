using Read2Me.Core.Models;

namespace Read2Me.Core.IO
{
    /// <summary>
    /// Turning a project-relative reference into a path on disk.
    /// <para>
    /// Persisted Book data stores forward-slashed, project-relative references — <c>audio/{item}.wav</c>,
    /// <c>voices/{character}/{voice}.wav</c> — so that a workspace can move. Every reader of one has to
    /// undo the same two steps, and doing it by hand is how a separator survives into a path on
    /// Windows.
    /// </para>
    /// </summary>
    public static class ProjectFilePaths
    {
        public static string ProjectFilePath(this IFileSystem fs, ProjectFolderId folder, string relativePath) =>
            Path.Combine(
                fs.GetProjectFolderPath(folder.Value),
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Removes a project-relative file if it is there. Callers that must not fail catch.</summary>
        public static void DeleteProjectFile(this IFileSystem fs, ProjectFolderId folder, string relativePath)
        {
            var path = fs.ProjectFilePath(folder, relativePath);
            if (fs.FileExists(path)) fs.DeleteFile(path);
        }
    }
}
