using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Llm;

namespace Read2Me.App.Api
{
    /// <summary>
    /// A cast-list row. <see cref="NarratesBook"/> is true on the character the narrator link
    /// points at; <see cref="IsNarrator"/> marks the seed Narrator row itself.
    /// </summary>
    public sealed record CharacterSummaryDto(
        Guid Id, string Name, IReadOnlyList<CharacterAliasDto> Aliases,
        int LineCount, int VoiceCount, int ReadyVoiceCount, bool IsNarrator, bool NarratesBook);

    /// <summary>One item a character speaks, with where it sits so the reader can open there.</summary>
    public sealed record CharacterLineDto(Guid ItemId, Guid ParagraphId, Guid ChapterId, string Text);

    /// <summary>
    /// An item in a context paragraph. <see cref="Speaker"/> is the character's name on a dialog
    /// item, null when the item is dialog whose speaker is not yet attributed; narration items are
    /// not dialog and carry no speaker.
    /// </summary>
    public sealed record ContextItemDto(Guid ItemId, string Text, bool IsDialog, string? Speaker);
    public sealed record ContextParagraphDto(string Text, IReadOnlyList<ContextItemDto> Items);
    /// <summary>A paragraph with its nearest neighbours in the same chapter, nearest last in <see cref="Before"/> and first in <see cref="After"/>.</summary>
    public sealed record ParagraphContextDto(
        IReadOnlyList<ContextParagraphDto> Before, ContextParagraphDto Paragraph, IReadOnlyList<ContextParagraphDto> After);

    /// <summary>The cast page's reads (Angular ticket 15): roster rows, a character's lines, a line's surroundings.</summary>
    public static class CharacterEndpoints
    {
        /// <summary>The widest context window a client may ask for on either side.</summary>
        public const int MaxContextWindow = 10;

        public static void MapCharacterEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/characters/summary", GetSummaryAsync)
                .WithSummary("Every character as a cast-list row (narrator first, then by name): aliases, line count, planned vs ready voices, isNarrator (the seed row) and narratesBook (the linked narrator).");
            endpoints.MapGet("/api/projects/{folder}/characters/{id:guid}/lines", GetLinesAsync)
                .WithSummary("The items a character speaks, in book order, with their paragraph and chapter ids. Empty for an unknown character.");
            endpoints.MapGet("/api/projects/{folder}/paragraphs/{paragraphId:guid}/context", GetContextAsync)
                .WithSummary("A paragraph with up to before/after neighbouring paragraphs of the same chapter (each 0..10, default 3/2) and the speaker name per item. 404 when the paragraph is not in the chapter.");
        }

        private static async Task<IResult> GetSummaryAsync(
            string folder, IFileSystem fs, ICharacterReader characters, IProjectCatalogReader catalog, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var rows = await characters.GetCharacterSummariesAsync(folderId);
            var narrator = await catalog.GetNarratorAsync(folderId, ct);
            return Results.Ok(rows.Select(r => new CharacterSummaryDto(
                r.Id, r.Name,
                r.Aliases.Select(a => new CharacterAliasDto(a.Id, a.Name)).ToList(),
                r.LineCount, r.VoiceCount, r.ReadyVoiceCount, r.IsNarrator,
                narrator.IsLinkedTo(r.Id))).ToList());
        }

        private static async Task<IResult> GetLinesAsync(string folder, Guid id, IFileSystem fs, ICharacterReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var lines = await reader.GetCharacterLinesAsync(folderId, id);
            return Results.Ok(lines.Select(l => new CharacterLineDto(l.ItemId, l.ParagraphId, l.ChapterId, l.Text)).ToList());
        }

        private static async Task<IResult> GetContextAsync(
            string folder, Guid paragraphId, Guid chapterId, IFileSystem fs, IBookContentReader reader,
            int before = 3, int after = 2)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var context = await reader.GetParagraphContextAsync(
                folderId, chapterId, paragraphId,
                Math.Clamp(before, 0, MaxContextWindow), Math.Clamp(after, 0, MaxContextWindow));
            if (context is null)
                return Results.NotFound();

            return Results.Ok(new ParagraphContextDto(
                context.Preceding.Select(ToDto).ToList(),
                ToDto(context.Query),
                context.Following.Select(ToDto).ToList()));
        }

        private static ContextParagraphDto ToDto(ContextParagraph p) => new(
            p.Text,
            p.Items.Select(i => i.Type == AttributionWire.Dialog
                ? new ContextItemDto(i.ItemId, i.Text, true, AttributionWire.IsUnknownSpeaker(i.Speaker) ? null : i.Speaker)
                : new ContextItemDto(i.ItemId, i.Text, false, null)).ToList());
    }
}
