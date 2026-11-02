using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
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

    public sealed record SeriesDto(Guid Id, string Name, IReadOnlyList<SeriesBookDto> Books)
    {
        public static SeriesDto From(SeriesView v) =>
            new(v.Id, v.Name, v.Books.Select(b => new SeriesBookDto(b.Folder, b.Position)).ToList());
    }

    public sealed record SeriesBookDto(string Folder, decimal Position);

    public sealed record CreateSeriesRequest(string? Name = null, string? Folder = null);

    public sealed record CreateSeriesResponse(Guid Id);

    public sealed record RenameSeriesRequest(string? Name = null);

    public sealed record JoinSeriesRequest(bool Move = false);

    public sealed record JoinSeriesResponse(decimal Position);

    public sealed record SeriesPositionsRequest(IReadOnlyDictionary<string, decimal>? Positions = null);
}
