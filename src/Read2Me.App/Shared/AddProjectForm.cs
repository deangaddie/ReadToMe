using Read2Me.Data.Enums;

namespace Read2Me.App.Shared;

public sealed class AddProjectForm
{
    public string Title { get; set; } = "";
    public string BookTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string? FileName { get; set; }
    public BookFileType? DetectedType { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Title) &&
        !string.IsNullOrWhiteSpace(BookTitle) &&
        !string.IsNullOrWhiteSpace(Author) &&
        FileName != null &&
        DetectedType != null;

    public void SetFile(string fileName)
    {
        FileName = fileName;
        DetectedType = System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".epub" => BookFileType.Epub,
            ".txt" => BookFileType.Text,
            _ => null,
        };
    }

    public void OnBookTitleBlur()
    {
        if (string.IsNullOrWhiteSpace(Title))
            Title = BookTitle;
    }
}
