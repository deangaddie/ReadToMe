using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Series;

namespace Read2Me.App.Api
{
    /// <summary>
    /// The series registry and its lifecycle (#213, #206). Books are addressed by folder. These
    /// writes change no project: they make no History step and ignore <c>X-Origin-Id</c>; each one
    /// sends the debounced <c>series</c> hub signal instead.
    /// </summary>
    public static class SeriesEndpoints
    {
        public static void MapSeriesEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/series", ListAsync)
                .WithSummary("Every series with its books (folder, position) in position order. Merge with GET /api/projects to show them.");
            endpoints.MapPost("/api/series", CreateAsync)
                .WithSummary("Create a series { name, folder? }; with a folder, that book joins as Book 1. " +
                             "400 blank name; 404 unknown folder; 409 name taken (ignoring case) or the book is in another series.");
            endpoints.MapPatch("/api/series/{id:guid}", RenameAsync)
                .WithSummary("Rename a series { name }. 400 blank; 404 unknown series; 409 name taken (ignoring case).");
            endpoints.MapDelete("/api/series/{id:guid}", DeleteAsync)
                .WithSummary("Delete a series: every book leaves and keeps its Voices, and the registry folder goes. 404 unknown series.");
            endpoints.MapPut("/api/series/{id:guid}/books/{folder}", JoinAsync)
                .WithSummary("Join a book { move? }: it gets the next whole number after the series' highest; already a member answers its position. " +
                             "A book in another series is 409 naming it, unless move is true (Move to: leave that one, then join). 404 unknown series or folder.");
            endpoints.MapDelete("/api/series/{id:guid}/books/{folder}", LeaveAsync)
                .WithSummary("A book leaves the series; nobody is renumbered and an empty series stays. 404 unknown series or folder, or a book not in it.");
            endpoints.MapPut("/api/series/{id:guid}/positions", SetPositionsAsync)
                .WithSummary("Save the whole position set { positions: { folder: number } }, checked together. 400 under 0 or more than two decimals; " +
                             "409 when the keys are not exactly the members (out of date) or two books share a number (naming both). 404 unknown series.");
            endpoints.MapGet("/api/series/{id:guid}/poster", GetPosterAsync)
                .Produces<Stream>(StatusCodes.Status200OK, "image/jpeg", "image/png", "image/webp")
                .WithSummary("The series poster: the uploaded image, else the cover of the lowest-positioned book. 404 unknown series or no image.");
            endpoints.MapPut("/api/series/{id:guid}/poster", SetPosterAsync)
                .WithSummary("Upload or replace the poster (multipart 'file': jpg/jpeg/png/webp, at most 10 MB, as the cover) → the series. " +
                             "400 wrong type or too big; 404 unknown series.");
            endpoints.MapDelete("/api/series/{id:guid}/poster", ClearPosterAsync)
                .WithSummary("Remove the uploaded poster: the fallback cover shows again. 204, also when there is none; 404 unknown series.");
            endpoints.MapGet("/api/series/{id:guid}/cast", CastAsync)
                .WithSummary("The series cast board: books in position order, one row per Series character (label = its Character's name in the lowest-positioned linked book) " +
                             "with one cell per book, and each book's unlinked Characters for the pickers. 404 unknown series.");
            endpoints.MapPost("/api/series/{id:guid}/characters", ShareAsync)
                .WithSummary("Share as new { folder, characterId }: a Series character made from that Character, which owns its Voices → 201 { seriesCharacterId }. " +
                             "404 unknown series, folder, Character or a book not in the series; 409 the Character is already linked.");
            endpoints.MapPut("/api/series/{id:guid}/characters/{seriesCharacterId:guid}/links/{folder}", LinkAsync)
                .WithSummary("Link { characterId } of the book to the Series character, running the Voice merge in each affected book as History steps under the same Voice Ids. " +
                             "404 unknown series, Series character, folder, Character or a book not in the series; 409 the Character is already linked, " +
                             "the book already links another Character to it (merge first), or one of its Voices is held by another Character of the book.");
            endpoints.MapDelete("/api/series/{id:guid}/characters/{seriesCharacterId:guid}/links/{folder}", UnlinkAsync)
                .WithSummary("Unlink the book's Character: the book keeps its copy of the Voices; a Series character left with no linked book is removed. " +
                             "404 unknown series, Series character, folder, a book not in the series, or nothing of the book linked to it.");
            endpoints.MapGet("/api/series/{id:guid}/characters/{seriesCharacterId:guid}/links/{folder}/preview", PreviewLinkAsync)
                .WithSummary("What linking characterId would copy, for the Link confirm: { bookGains, seriesGains } (Voice names). Refused as the link itself would be.");
            endpoints.MapGet("/api/series/{id:guid}/characters/{seriesCharacterId:guid}", CharacterAsync)
                .WithSummary("The inline series Voice editor's read: the series copy's folder and its Voices (edit them through that book's per-book routes; " +
                             "each commit is copied into every other linked book; one made here brings each differing book back in line), each linked book in position order with its Character, " +
                             "default Voice and differs (its Voices are not the series copy: the version most linked books hold, ties to the lowest position), " +
                             "and deleteBlocks by Voice Id (PositionalRules or LastVoice, naming the books). 404 unknown series or Series character.");
            endpoints.MapPost("/api/series/{id:guid}/characters/{seriesCharacterId:guid}/push", PushAsync)
                .WithSummary("Push { folder }: that book's Voices of the Series character are copied into every other linked book, each a \"Push series voices\" History step there. " +
                             "Only Voices, never Voice Rules. 404 unknown series, Series character, folder, a book not in the series or not linked to it; " +
                             "409 the book doesn't differ from series; 422 naming the books a copy failed to write into (the rest were written).");
            endpoints.MapPost("/api/series/{id:guid}/characters/{seriesCharacterId:guid}/take", TakeAsync)
                .WithSummary("Take { folder }: the series copy's Voices are copied into that book, a \"Take series voices\" History step there. " +
                             "Only Voices, never Voice Rules. 404 as push; 409 the book doesn't differ from series; 422 the copy failed to write.");
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> PushAsync(
            Guid id, Guid seriesCharacterId, SeriesResolveRequest? body, SeriesRegistry registry, CancellationToken ct) =>
            body?.Folder is { Length: > 0 } folder
                ? NoContentOr(await registry.PushAsync(id, seriesCharacterId, folder, ct))
                : TypedResults.Problem("Expected a JSON body with 'folder'.", statusCode: StatusCodes.Status400BadRequest);

        private static async Task<Results<NoContent, ProblemHttpResult>> TakeAsync(
            Guid id, Guid seriesCharacterId, SeriesResolveRequest? body, SeriesRegistry registry, CancellationToken ct) =>
            body?.Folder is { Length: > 0 } folder
                ? NoContentOr(await registry.TakeAsync(id, seriesCharacterId, folder, ct))
                : TypedResults.Problem("Expected a JSON body with 'folder'.", statusCode: StatusCodes.Status400BadRequest);

        private static Results<NoContent, ProblemHttpResult> NoContentOr(SeriesResult<bool> result) =>
            result.Refusal is { } refusal ? Refused(refusal) : TypedResults.NoContent();

        private static async Task<Results<PhysicalFileHttpResult, NotFound, ProblemHttpResult>> GetPosterAsync(
            Guid id, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.GetAsync(id, ct);
            if (result.Refusal is { } refusal) return Refused(refusal);
            if (result.Value!.Poster.Path is not { } path) return TypedResults.NotFound();

            // The URL carries ?v= that changes with the file, so the browser may cache it; ETag and
            // Last-Modified still answer a plain reload.
            var type = ImageUpload.ContentTypeOf(path);
            return TypedResults.PhysicalFile(path, type, lastModified: File.GetLastWriteTimeUtc(path));
        }

        private static async Task<Results<Ok<SeriesDto>, ProblemHttpResult>> SetPosterAsync(
            Guid id, HttpRequest request, SeriesRegistry registry, CancellationToken ct)
        {
            var (upload, uploadRefusal) = await UploadedFile.ReadAsync(
                request, ImageUpload.Extensions, ImageUpload.MaxBytes, "Poster image must be 10 MB or smaller.", ct);
            if (upload is null) return uploadRefusal!;

            await using var stream = upload.File.OpenReadStream();
            var result = await registry.SetPosterAsync(id, upload.Extension, stream, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.Ok(SeriesDto.From(result.Value!));
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> ClearPosterAsync(Guid id, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.ClearPosterAsync(id, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.NoContent();
        }

        private static async Task<Ok<IReadOnlyList<SeriesDto>>> ListAsync(SeriesRegistry registry, CancellationToken ct)
        {
            var all = await registry.ListAsync(ct);
            return TypedResults.Ok<IReadOnlyList<SeriesDto>>(all.Select(SeriesDto.From).ToList());
        }

        private static async Task<Results<Created<CreateSeriesResponse>, ProblemHttpResult>> CreateAsync(
            CreateSeriesRequest? body, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.CreateAsync(body?.Name, body?.Folder, ct);
            return result.Refusal is { } refusal
                ? Refused(refusal)
                : TypedResults.Created($"/api/series/{result.Value}", new CreateSeriesResponse(result.Value));
        }

        private static async Task<Results<Ok<SeriesDto>, ProblemHttpResult>> RenameAsync(
            Guid id, RenameSeriesRequest? body, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.RenameAsync(id, body?.Name, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.Ok(SeriesDto.From(result.Value!));
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.DeleteAsync(id, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.NoContent();
        }

        private static async Task<Results<Ok<JoinSeriesResponse>, ProblemHttpResult>> JoinAsync(
            Guid id, string folder, JoinSeriesRequest? body, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.JoinAsync(id, folder, body?.Move ?? false, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.Ok(new JoinSeriesResponse(result.Value));
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> LeaveAsync(
            Guid id, string folder, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.LeaveAsync(id, folder, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.NoContent();
        }

        private static async Task<Results<Ok<SeriesDto>, ProblemHttpResult>> SetPositionsAsync(
            Guid id, SeriesPositionsRequest? body, SeriesRegistry registry, CancellationToken ct)
        {
            if (body?.Positions is null)
                return TypedResults.Problem("Expected a JSON body with 'positions'.", statusCode: StatusCodes.Status400BadRequest);

            var result = await registry.SetPositionsAsync(id, body.Positions, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.Ok(SeriesDto.From(result.Value!));
        }

        // ---- Series characters and links (#220) ----------------------------------------------------

        private static async Task<Results<Ok<SeriesCastDto>, ProblemHttpResult>> CastAsync(Guid id, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.CastAsync(id, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.Ok(SeriesCastDto.From(result.Value!));
        }

        private static async Task<Results<Created<ShareSeriesCharacterResponse>, ProblemHttpResult>> ShareAsync(
            Guid id, ShareSeriesCharacterRequest? body, SeriesRegistry registry, CancellationToken ct)
        {
            if (body?.Folder is not { Length: > 0 } folder || body.CharacterId is not { } characterId)
                return TypedResults.Problem("Expected a JSON body with 'folder' and 'characterId'.", statusCode: StatusCodes.Status400BadRequest);

            var result = await registry.ShareAsync(id, folder, characterId, ct);
            return result.Refusal is { } refusal
                ? Refused(refusal)
                : TypedResults.Created($"/api/series/{id}/characters/{result.Value}", new ShareSeriesCharacterResponse(result.Value));
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> LinkAsync(
            Guid id, Guid seriesCharacterId, string folder, LinkSeriesCharacterRequest? body, SeriesRegistry registry, CancellationToken ct)
        {
            if (body?.CharacterId is not { } characterId)
                return TypedResults.Problem("Expected a JSON body with 'characterId'.", statusCode: StatusCodes.Status400BadRequest);

            var result = await registry.LinkAsync(id, seriesCharacterId, folder, characterId, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.NoContent();
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> UnlinkAsync(
            Guid id, Guid seriesCharacterId, string folder, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.UnlinkAsync(id, seriesCharacterId, folder, ct);
            return result.Refusal is { } refusal ? Refused(refusal) : TypedResults.NoContent();
        }

        private static async Task<Results<Ok<SeriesLinkPreviewDto>, ProblemHttpResult>> PreviewLinkAsync(
            Guid id, Guid seriesCharacterId, string folder, Guid characterId, SeriesRegistry registry, CancellationToken ct)
        {
            var result = await registry.PreviewLinkAsync(id, seriesCharacterId, folder, characterId, ct);
            return result.Refusal is { } refusal
                ? Refused(refusal)
                : TypedResults.Ok(new SeriesLinkPreviewDto(result.Value!.BookGains, result.Value.SeriesGains));
        }

        private static async Task<Results<Ok<SeriesCharacterDto>, ProblemHttpResult>> CharacterAsync(
            Guid id, Guid seriesCharacterId, SeriesRegistry registry, IVoiceOriginalStore originals, IFileSystem fs, CancellationToken ct)
        {
            var result = await registry.CharacterAsync(id, seriesCharacterId, ct);
            if (result.Refusal is { } refusal) return Refused(refusal);
            var view = result.Value!;
            var copyFolder = new ProjectFolderId(view.SeriesCopyFolder);
            return TypedResults.Ok(new SeriesCharacterDto(
                view.SeriesCopyFolder,
                view.Voices.Select(v => VoiceEndpoints.ToDto(copyFolder, v, originals, fs)).ToList(),
                view.Books.Select(b => new SeriesCharacterBookDto(
                    b.Folder, b.Title, b.Position, b.CharacterId, b.CharacterName, b.DefaultVoiceId, b.Differs)).ToList(),
                view.DeleteBlocks.ToDictionary(
                    d => d.Key.ToString(),
                    d => new SeriesVoiceDeleteBlockDto(
                        d.Value.Reason,
                        d.Value.Books.Select(b => new SeriesVoiceDeleteBookDto(b.Folder, b.Title, b.RuleCount)).ToList()))));
        }

        private static ProblemHttpResult Refused(SeriesRefusal refusal) => TypedResults.Problem(
            refusal.Detail,
            statusCode: refusal.Kind switch
            {
                SeriesRefusalKind.Malformed => StatusCodes.Status400BadRequest,
                SeriesRefusalKind.NotFound => StatusCodes.Status404NotFound,
                SeriesRefusalKind.Blocked => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status422UnprocessableEntity,
            });
    }

    public sealed record SeriesDto(Guid Id, string Name, SeriesPosterDto Poster, IReadOnlyList<SeriesBookDto> Books)
    {
        public static SeriesDto From(SeriesView v) =>
            new(v.Id, v.Name, SeriesPosterDto.From(v.Id, v.Poster), v.Books.Select(b => new SeriesBookDto(b.Folder, b.Position)).ToList());
    }

    /// <summary>
    /// The series card's image (#219): <see cref="Url"/> is null when <see cref="Source"/> is None;
    /// <see cref="CoverPosition"/> is the book whose cover stands in, only for Cover.
    /// </summary>
    public sealed record SeriesPosterDto(string? Url, SeriesPosterSource Source, decimal? CoverPosition)
    {
        public static SeriesPosterDto From(Guid seriesId, SeriesPoster poster) => new(
            poster.Path is null ? null : $"/api/series/{seriesId}/poster?v={poster.Version}",
            poster.Source,
            poster.CoverPosition);
    }

    public sealed record SeriesBookDto(string Folder, decimal Position);

    public sealed record CreateSeriesRequest(string? Name = null, string? Folder = null);

    public sealed record CreateSeriesResponse(Guid Id);

    public sealed record RenameSeriesRequest(string? Name = null);

    public sealed record JoinSeriesRequest(bool Move = false);

    public sealed record JoinSeriesResponse(decimal Position);

    public sealed record ShareSeriesCharacterRequest(string? Folder = null, Guid? CharacterId = null);

    public sealed record ShareSeriesCharacterResponse(Guid SeriesCharacterId);

    public sealed record LinkSeriesCharacterRequest(Guid? CharacterId = null);

    /// <summary>The Link confirm's gains (#208, 7): the Voice names this book gains, and those the other linked books gain.</summary>
    public sealed record SeriesLinkPreviewDto(IReadOnlyList<string> BookGains, IReadOnlyList<string> SeriesGains);

    /// <summary>
    /// The series cast board (#213): <see cref="SuggestedRows"/>, each cell's <c>suggested</c>, each
    /// unlinked Character's <c>nameMatches</c> and <see cref="SuggestionCount"/> stay empty until link
    /// suggestions (#223).
    /// </summary>
    public sealed record SeriesCastDto(
        IReadOnlyList<SeriesCastBookDto> Books,
        IReadOnlyList<SeriesCastRowDto> Rows,
        IReadOnlyList<SeriesSuggestedRowDto> SuggestedRows,
        IReadOnlyDictionary<string, IReadOnlyList<SeriesUnlinkedCharacterDto>> Unlinked,
        int SuggestionCount)
    {
        public static SeriesCastDto From(SeriesCastView v) => new(
            v.Books.Select(b => new SeriesCastBookDto(b.Folder, b.Title, b.Position)).ToList(),
            v.Rows.Select(r => new SeriesCastRowDto(
                r.SeriesCharacterId, r.Label, r.IsNarrator,
                r.Cells.Select(c => new SeriesCastCellDto(
                    c.Folder,
                    c.Linked is { } l ? new SeriesLinkedCellDto(l.CharacterId, l.Name, c.Differs) : null,
                    Suggested: null)).ToList())).ToList(),
            [],
            v.Unlinked.ToDictionary(
                u => u.Key,
                u => (IReadOnlyList<SeriesUnlinkedCharacterDto>)u.Value
                    .Select(c => new SeriesUnlinkedCharacterDto(c.CharacterId, c.Name, c.IsNarrator, [])).ToList()),
            SuggestionCount: 0);
    }

    public sealed record SeriesCastBookDto(string Folder, string Title, decimal Position);

    public sealed record SeriesCastRowDto(Guid SeriesCharacterId, string Label, bool IsNarrator, IReadOnlyList<SeriesCastCellDto> Cells);

    /// <summary>One book's cell: the Character linked there, a suggestion, or neither (an empty cell, with a picker).</summary>
    public sealed record SeriesCastCellDto(string Folder, SeriesLinkedCellDto? Linked, SeriesCharacterRefDto? Suggested);

    public sealed record SeriesLinkedCellDto(Guid CharacterId, string Name, bool Differs);

    public sealed record SeriesCharacterRefDto(Guid CharacterId, string Name);

    public sealed record SeriesSuggestedRowDto(string Label, IReadOnlyList<SeriesSuggestedMemberDto> Members);

    public sealed record SeriesSuggestedMemberDto(string Folder, Guid CharacterId, string Name);

    /// <summary>A book's unlinked Character, for the pickers; <see cref="NameMatches"/> marks the rows whose names it matches (#223).</summary>
    public sealed record SeriesUnlinkedCharacterDto(Guid CharacterId, string Name, bool IsNarrator, IReadOnlyList<Guid> NameMatches);

    /// <summary>A Character's series link on the Cast page's roster; <see cref="Differs"/>: this book's Voices are not the series copy (#222).</summary>
    public sealed record CharacterSeriesLinkDto(Guid SeriesId, Guid SeriesCharacterId, string SeriesName, int BookCount, bool Differs);

    /// <summary>
    /// The inline series Voice editor (#213): <see cref="Voices"/> come from the series copy, whose
    /// folder takes the editor's writes; <see cref="DeleteBlocks"/> is keyed by Voice Id.
    /// </summary>
    public sealed record SeriesCharacterDto(
        string SeriesCopyFolder,
        IReadOnlyList<VoiceDto> Voices,
        IReadOnlyList<SeriesCharacterBookDto> Books,
        IReadOnlyDictionary<string, SeriesVoiceDeleteBlockDto> DeleteBlocks);

    /// <summary>A linked book, its Character and its own default Voice; <see cref="Differs"/>: its Voices are not the series copy (#222).</summary>
    public sealed record SeriesCharacterBookDto(
        string Folder, string Title, decimal Position, Guid CharacterId, string CharacterName, Guid? DefaultVoiceId, bool Differs);

    /// <summary>Why a Voice can't be deleted, and the books that stop it.</summary>
    public sealed record SeriesVoiceDeleteBlockDto(SeriesVoiceDeleteReason Reason, IReadOnlyList<SeriesVoiceDeleteBookDto> Books);

    public sealed record SeriesVoiceDeleteBookDto(string Folder, string Title, int RuleCount);

    public sealed record SeriesPositionsRequest(IReadOnlyDictionary<string, decimal>? Positions = null);

    /// <summary>Push or take: the book whose Voices are pushed, or that takes the series copy.</summary>
    public sealed record SeriesResolveRequest(string? Folder = null);
}
