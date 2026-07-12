using Read2Me.Data.Entities;

namespace Read2Me.App.Api
{
    public sealed record CreateProjectResponse(string FolderName);

    public sealed record ImportRequest(bool Reread = false);

    public sealed record ProjectDetailDto(
        string FolderName,
        string Title,
        string BookTitle,
        string Author,
        string Filename,
        string FileType,
        string? CoverImage,
        bool NarratorOnlyMode)
    {
        public static ProjectDetailDto From(string folderName, Project p) => new(
            folderName, p.Title, p.BookTitle, p.Author, p.Filename,
            p.Type.ToString(), p.CoverImage, p.NarratorOnlyMode);
    }
}
