using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.App.Api
{
    public sealed record CreateProjectResponse(string FolderName);

    public sealed record ImportRequest(bool Reread = false);

    /// <summary>
    /// Who narrates the book, projected from <see cref="NarratorIdentity"/> — the raw link
    /// column never goes on the wire. Unlinked serialises as
    /// <see cref="NarratorIdentity.Unlinked"/> (the seed row's id, "Narrator", false), so
    /// there is no null case at either end.
    /// </summary>
    public sealed record NarratorDto(Guid CharacterId, string DisplayName, bool IsLinked)
    {
        public static NarratorDto From(NarratorIdentity n) => new(n.CharacterId, n.DisplayName, n.IsLinked);
    }

    public sealed record ProjectDetailDto(
        string FolderName,
        string Title,
        string BookTitle,
        string Author,
        string Filename,
        string FileType,
        string? CoverImage,
        bool NarratorOnlyMode,
        NarratorDto Narrator)
    {
        public static ProjectDetailDto From(string folderName, Project p, NarratorIdentity narrator) => new(
            folderName, p.Title, p.BookTitle, p.Author, p.Filename,
            p.Type.ToString(), p.CoverImage, p.NarratorOnlyMode, NarratorDto.From(narrator));
    }
}
