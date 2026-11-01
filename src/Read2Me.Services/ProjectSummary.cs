using Read2Me.Data.Enums;

namespace Read2Me.Services
{
    /// <summary>One row of the project list. <see cref="FileType"/> is null for a folder whose DB could not be opened.</summary>
    public record ProjectSummary(
        string FolderName,
        string Title,
        string? Author = null,
        string? CoverImage = null,
        int AudioItemTotal = 0,
        int AudioItemDone = 0,
        BookFileType? FileType = null)
    {
        public int AudioPercent => AudioItemTotal == 0 ? 0 : (int)(100.0 * AudioItemDone / AudioItemTotal);
    }
}
