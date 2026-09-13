using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Shared;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Voice;

namespace Read2Me.App.Api
{
    public sealed record NodeDto(Guid Id, string? Title);

    /// <summary>
    /// <see cref="OrderKey"/> is the item's fractional position key within its paragraph (items
    /// arrive already in that order). <see cref="ItemType"/> stays for older clients; new ones read
    /// <see cref="IsPause"/> and the speaker.
    /// </summary>
    public sealed record ParagraphItemDto(
        Guid Id, string ItemType, string? Text, Guid? CharacterId, string? AudioFileName,
        string? VoiceInstructions, string OrderKey, bool IsPause);

    /// <summary><see cref="IsPauseParagraph"/>: no items, or a single pause item.</summary>
    public sealed record ParagraphDto(Guid Id, IReadOnlyList<ParagraphItemDto> Items, bool IsPauseParagraph);

    /// <summary>
    /// The Voice a speech item will be spoken in. <see cref="VoiceName"/> is null when no Voice
    /// resolves; <see cref="NarratedBy"/> is the linked narrator's name on a narration item.
    /// </summary>
    public sealed record ItemVoiceDto(string? VoiceName, string? NarratedBy);
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
            endpoints.MapGet("/api/projects/{folder}/nodes/chapter/{id:guid}/voices", GetChapterVoicesAsync)
                .WithSummary("Resolved voice per speech item of a chapter: { itemId: { voiceName, narratedBy } }. voiceName null = no voice resolves; narratedBy = the linked narrator's name on narration items.");
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

        private static async Task<IResult> GetChapterVoicesAsync(
            string folder, Guid id, IFileSystem fs, IBookContentReader content,
            IProjectCatalogReader catalog, IVoiceResolver voices, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var children = await content.GetChildrenAsync(folderId, BookNodeLevel.Chapter, id);
            var items = (children.Paragraphs ?? [])
                .SelectMany(p => p.Items)
                .Where(i => !ParagraphItemKinds.IsPause(i.ItemType))
                .ToList();
            if (items.Count == 0)
                return Results.Ok(new Dictionary<string, ItemVoiceDto>());

            var names = await voices.ResolveNamesAsync(folderId, items.Select(i => i.Id).ToList(), ct);
            var narrator = await catalog.GetNarratorAsync(folderId, ct);
            return Results.Ok(items.ToDictionary(
                i => i.Id.ToString(),
                i => new ItemVoiceDto(
                    names.GetValueOrDefault(i.Id),
                    narrator.IsLinked && NarrationRule.IsNarration(i) ? narrator.DisplayName : null)));
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
                i.Id, ItemTypeWord(i), i.Text, i.CharacterId, i.AudioFileName,
                i.VoiceInstructions, i.Order, ParagraphItemKinds.IsPause(i.ItemType))).ToList(),
            ParagraphItemDisplay.IsPauseParagraph(p));

        /// <summary>
        /// The word an API client sees for an item's kind. Storage no longer distinguishes narration
        /// from dialog — the speaker does (ADR-0006) — so the word is computed: narration when the
        /// speaker is the narrator sentinel, dialog otherwise (an unattributed item included), pause
        /// kinds by name. The spelling is the one the API has always emitted, so no client changes
        /// and <c>FrozenItemBoundaryTests</c> passes unmodified.
        /// </summary>
        private static string ItemTypeWord(ParagraphItem item) =>
            ParagraphItemKinds.IsPause(item.ItemType) ? item.ItemType.ToString()
            : NarrationRule.IsNarration(item) ? "Narration"
            : "Character";
    }
}
