using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;

namespace Read2Me.App.Api
{
    public sealed record NodeDto(Guid Id, string? Title);
    public sealed record ParagraphItemDto(Guid Id, string ItemType, string? Text, Guid? CharacterId, string? AudioFileName);
    public sealed record ParagraphDto(Guid Id, IReadOnlyList<ParagraphItemDto> Items);
    public sealed record NodeChildrenDto(
        IReadOnlyList<NodeDto>? Parts,
        IReadOnlyList<NodeDto>? Chapters,
        IReadOnlyList<ParagraphDto>? Paragraphs);
    public sealed record CharacterAliasDto(Guid Id, string Name);
    public sealed record CharacterDto(Guid Id, string Name, IReadOnlyList<CharacterAliasDto> Aliases);
    public sealed record BookOverviewDto(
        bool HasContent,
        IReadOnlyList<NodeDto> Volumes,
        IReadOnlyList<CharacterDto> Characters,
        int TotalParts,
        int TotalChapters);

    public static class BookEndpoints
    {
        public static void MapBookEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/book", GetOverviewAsync)
                .WithSummary("Book overview: volumes, characters and structure counts. hasContent=false means import has not run.");
            endpoints.MapGet("/api/projects/{folder}/nodes/{level}/{id:guid}/children", GetChildrenAsync)
                .WithSummary("Ordered children of a node. level=volume gives parts, part gives chapters, chapter gives paragraphs with their items.");
            endpoints.MapGet("/api/projects/{folder}/characters", GetCharactersAsync)
                .WithSummary("All characters with their aliases.");
        }

        private static async Task<IResult> GetOverviewAsync(string folder, IFileSystem fs, IBookContentReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var overview = await reader.GetBookOverviewAsync(folderId);
            return Results.Ok(new BookOverviewDto(
                overview.HasContent,
                overview.Volumes.Select(v => new NodeDto(v.Id, v.Title)).ToList(),
                overview.Characters.Select(ToCharacterDto).ToList(),
                overview.TotalParts,
                overview.TotalChapters));
        }

        private static async Task<IResult> GetChildrenAsync(
            string folder, string level, Guid id, IFileSystem fs, IBookContentReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (!Enum.TryParse<BookNodeLevel>(level, ignoreCase: true, out var nodeLevel))
                return Results.Problem($"Unknown level '{level}'. Expected volume, part or chapter.",
                    statusCode: StatusCodes.Status400BadRequest);

            var children = await reader.GetChildrenAsync(folderId, nodeLevel, id);
            return Results.Ok(new NodeChildrenDto(
                children.Parts?.Select(p => new NodeDto(p.Id, p.Title)).ToList(),
                children.Chapters?.Select(c => new NodeDto(c.Id, c.Title)).ToList(),
                children.Paragraphs?.Select(ToParagraphDto).ToList()));
        }

        private static async Task<IResult> GetCharactersAsync(string folder, IFileSystem fs, ICharacterReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var characters = await reader.GetCharactersWithAliasesAsync(folderId);
            return Results.Ok(characters.Select(ToCharacterDto).ToList());
        }

        private static CharacterDto ToCharacterDto(Character c) => new(
            c.Id, c.Name, c.Aliases.Select(a => new CharacterAliasDto(a.Id, a.Name)).ToList());

        private static ParagraphDto ToParagraphDto(Paragraph p) => new(
            p.Id,
            p.Items.Select(i => new ParagraphItemDto(
                i.Id, i.ItemType.ToString(), i.Text, i.CharacterId, i.AudioFileName)).ToList());
    }
}
