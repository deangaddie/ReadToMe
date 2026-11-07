using Read2Me.Data.Enums;
using Read2Me.Services.Series;

namespace Read2Me.Services
{
    /// <summary>
    /// One row of the project list (<c>GET /api/projects</c>). <see cref="FileType"/> is null for a folder
    /// whose DB could not be opened.
    /// </summary>
    public record ProjectSummary(
        string FolderName,
        string Title,
        string? Author,
        string? CoverImage,
        int AudioItemTotal,
        int AudioItemDone,
        BookFileType? FileType)
    {
        /// <summary>The row for a folder whose project can't be read: its name as the title, nothing counted.</summary>
        public static ProjectSummary Unreadable(string folderName) => new(folderName, folderName, null, null, 0, 0, null);

        /// <summary>The series the project is in, or null. Filled in by the API from the series registries.</summary>
        public ProjectSeries? Series { get; init; }

        public int AudioPercent => AudioItemTotal == 0 ? 0 : (int)(100.0 * AudioItemDone / AudioItemTotal);
    }
}
