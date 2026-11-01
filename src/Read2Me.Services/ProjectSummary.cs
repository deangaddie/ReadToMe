using Read2Me.Data.Enums;

namespace Read2Me.Services
{
    /// <summary>One row of the project list. <see cref="FileType"/> is null for a folder whose DB could not be opened.</summary>
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

        public int AudioPercent => AudioItemTotal == 0 ? 0 : (int)(100.0 * AudioItemDone / AudioItemTotal);
    }
}
