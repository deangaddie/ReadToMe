using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Services;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Pronunciations;

namespace Read2Me.App.Api
{
    public sealed record AddPronunciationRequest(string? WrittenForm, string? SpokenForm);

    /// <summary>
    /// The book's Pronunciation list: written form → spoken form, applied to what TTS is asked to say
    /// (the item's Spoken text), never to the book text.
    /// </summary>
    public static class PronunciationEndpoints
    {
        public static void MapPronunciationEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/pronunciations", ListAsync)
                .ReadsAt()
                .WithSummary("The Pronunciation list, sorted by written form: id, writtenForm, spokenForm, and its reach in the book — " +
                             "matchCount (matched spans), itemCount (items holding one) and withAudioCount (those items that have audio).");
            endpoints.MapPost("/api/projects/{folder}/pronunciations", AddAsync)
                .RecordsOwnStep()
                .Produces<PronunciationEntry>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .WithSummary("Add a Pronunciation { writtenForm, spokenForm }. Both are trimmed and inner whitespace collapsed to one space. " +
                             $"Written form: 1–{PronunciationForms.MaxWrittenLength} characters, starting and ending with a letter or digit; " +
                             $"spoken form: 1–{PronunciationForms.MaxSpokenLength} characters. 201 with the entry and its reach; 400 invalid; " +
                             "409 when another entry has the written form, ignoring case. One History step, labelled Pronunciation: add \"<written>\". " +
                             "An X-Origin-Id header (GUID) is echoed as originId on the mutation receipt the live hub publishes.");
        }

        private static async Task<IResult> ListAsync(string folder, IFileSystem fs, ProjectDbSession session, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            return Results.Ok(await PronunciationList.ReadAsync(await session.OpenAsync(folderId), ct));
        }

        private static async Task<IResult> AddAsync(
            string folder, AddPronunciationRequest? body, HttpRequest request, IFileSystem fs, BookMutations mutations,
            MutationOrigin origin, HistoryStepScope steps, ProjectDbSession session, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var written = PronunciationForms.Normalise(body?.WrittenForm);
            OriginHeader.Apply(request, origin);
            BookMutationOutcome outcome;
            // A refused add commits nothing, so its step has no chunk and is dropped.
            await using (steps.Open(folderId, $"Pronunciation: add \"{written}\"", StepAction.Edit))
                outcome = await mutations.CommitAsync(
                    new AddPronunciationMutation(folderId, body?.WrittenForm ?? "", body?.SpokenForm ?? ""), ct);

            switch (outcome)
            {
                case BookMutationOutcome.Committed { Receipt.Effects.CreatedId: { } id }:
                    var entry = (await PronunciationList.ReadAsync(await session.OpenAsync(folderId), ct)).Single(e => e.Id == id);
                    return Results.Created($"/api/projects/{folder}/pronunciations/{id}", entry);
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled:
                    throw new OperationCanceledException(cancelled.Message, ct);
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.Validation } invalid:
                    return Results.Problem(invalid.Message, statusCode: StatusCodes.Status400BadRequest);
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.Conflict or BookMutationRejection.Restoring } conflict:
                    return Results.Problem(conflict.Message, statusCode: StatusCodes.Status409Conflict);
                case BookMutationOutcome.Rejected rejected:
                    return Results.Problem(rejected.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                default:
                    throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}.");
            }
        }
    }
}
