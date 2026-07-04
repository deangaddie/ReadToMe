namespace Read2Me.Core.Models
{
    public record ProjectSummary(
        string FolderName,
        string Title,
        string? Author = null,
        string? CoverImage = null,
        int AudioItemTotal = 0,
        int AudioItemDone = 0)
    {
        public int AudioPercent => AudioItemTotal == 0 ? 0 : (int)(100.0 * AudioItemDone / AudioItemTotal);
    }
}
