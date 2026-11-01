using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Read2Me.App.Characters;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Characters;
using Read2Me.Services.Health;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Pronunciations;

namespace Read2Me.App.Api
{
    public sealed record SetPronunciationRequest(string? WrittenForm, string? SpokenForm);

    public sealed record PronunciationDraftRequest(Guid? Id, string? WrittenForm, string? SpokenForm, bool Delete = false);

    /// <summary>
    /// A try-out: the sentence of <paramref name="ItemId"/> starting at <paramref name="SentenceStart"/> (an impact
    /// match's <c>sentenceStart</c>), with <paramref name="Draft"/> in its entry's place unless <paramref name="AsWritten"/>.
    /// <paramref name="Voice"/> is <c>{ "voiceId": "…" }</c> or <c>"item"</c> (the item's own resolved voice, also when left out).
    /// </summary>
    public sealed record TryPronunciationRequest(
        Guid ItemId, int SentenceStart, PronunciationDraftRequest? Draft, bool AsWritten, JsonElement? Voice);

    /// <summary>
    /// A saved entry with its reach, and what saving it cost: <paramref name="AudioDiscardCount"/> items lost
    /// their audio, and <paramref name="NeedsAudioCount"/> spoken items now have none.
    /// </summary>
    public sealed record PronunciationSaved(
        Guid Id, string WrittenForm, string SpokenForm, int MatchCount, int ItemCount, int WithAudioCount,
        int AudioDiscardCount, int NeedsAudioCount);

    /// <summary>What deleting an entry cost, counted as for <see cref="PronunciationSaved"/>.</summary>
    public sealed record PronunciationDeleted(int AudioDiscardCount, int NeedsAudioCount);

    /// <summary>
    /// The book's Pronunciation list: written form → spoken form, applied to what TTS is asked to say
    /// (the item's Spoken text), never to the book text. A change discards the audio of every item that
    /// has audio and whose Spoken text it alters.
    /// </summary>
    public static class PronunciationEndpoints
    {
        private const string ChangeCost =
            "Items with audio whose Spoken text the change alters lose it (WAV reference and audio review), as a text edit does; " +
            "audioDiscardCount counts them, needsAudioCount the spoken items without audio after it. " +
            "409 while audio generation is queued or running for the project, or a Restore runs. " +
            "An X-Origin-Id header (GUID) is echoed as originId on the mutation receipt the live hub publishes.";

        public static void MapPronunciationEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/pronunciations", ListAsync)
                .ReadsAt()
                .WithSummary("The Pronunciation list, sorted by written form: id, writtenForm, spokenForm, and its reach in the book — " +
                             "matchCount (matched spans), itemCount (items holding one) and withAudioCount (those items that have audio).");
            endpoints.MapGet("/api/projects/{folder}/pronunciations/matches", MatchesAsync)
                .ReadsAt()
                .Produces<PronunciationSpanReport>()
                .WithSummary("Where the list matches in the chapters named by repeated chapterIds (the Book view's marks): spans in book " +
                             "order — itemId, start and length in the item's text, pronunciationId, and sentenceStart (where the sentence " +
                             "around it starts, what a try-out names it by) — and pronunciations, the entries they name { id, writtenForm, " +
                             "spokenForm }, sorted by written form. Unknown chapters match nothing.");
            endpoints.MapPost("/api/projects/{folder}/pronunciations", AddAsync)
                .RecordsOwnStep()
                .Produces<PronunciationSaved>(StatusCodes.Status201Created)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithSummary("Add a Pronunciation { writtenForm, spokenForm }. Both are trimmed and inner whitespace collapsed to one space. " +
                             $"Written form: 1–{PronunciationForms.MaxWrittenLength} characters, starting and ending with a letter or digit; " +
                             $"spoken form: 1–{PronunciationForms.MaxSpokenLength} characters. 201 with the entry, its reach and what it cost; 400 invalid; " +
                             "409 when another entry has the written form, ignoring case. One History step, labelled Pronunciation: add \"<written>\". " +
                             ChangeCost);
            endpoints.MapPut("/api/projects/{folder}/pronunciations/{id:guid}", EditAsync)
                .RecordsOwnStep()
                .Produces<PronunciationSaved>()
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithSummary("Edit a Pronunciation's { writtenForm, spokenForm }, checked as an add. 200 with the entry, its reach and what it cost; " +
                             "400 invalid; 404 unknown id; 409 when another entry has the written form. Saving it unchanged writes nothing and lands no step; " +
                             "otherwise one History step, labelled Pronunciation: edit \"<written>\". " + ChangeCost);
            endpoints.MapDelete("/api/projects/{folder}/pronunciations/{id:guid}", DeleteAsync)
                .RecordsOwnStep()
                .Produces<PronunciationDeleted>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithSummary("Delete a Pronunciation: its word goes back to how the provider reads it. 404 unknown id. " +
                             "One History step, labelled Pronunciation: delete \"<written>\". " + ChangeCost);
            endpoints.MapPost("/api/projects/{folder}/pronunciations/impact", ImpactAsync)
                .RecordsNoStep("A dry run: it reads the list and the book and writes nothing.")
                .Produces<PronunciationImpactReport>()
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .WithSummary("What saving a draft { id?, writtenForm, spokenForm, delete? } would do, writing nothing: no id adds, an id edits " +
                             "that entry, delete: true deletes it (forms ignored). Answers matchCount, itemCount, withAudioCount (a delete's: the entry's " +
                             "reach now), audioDiscardCount (exactly what the save would discard), overlaps (entries whose written form holds the draft's " +
                             "as whole words or is held by it, draftWins when the draft's is the longer and so wins), duplicateOf (another entry with the " +
                             "written form, ignoring case: a save would be 409; the counts treat the draft as taking its place), and matches in book order: " +
                             "itemId, chapterId, chapterTitle, speaker, hasAudio, the sentence around it, sentenceStart (where it starts in the item's text, " +
                             "what a try-out names it by), matchStart and matchLength in that sentence, " +
                             "spokenSentence, the sentence with the drafted list applied, and spokenMatchStart and spokenMatchLength, where the match's " +
                             "spoken form (its case carried over) sits in spokenSentence. 400 invalid; 404 unknown id.");
            endpoints.MapPost("/api/projects/{folder}/pronunciations/try", TryAsync).RefusedWhileSetupCheckRuns()
                .RecordsNoStep("A try-out: it renders a sentence and stores nothing.")
                .Produces(StatusCodes.Status200OK, contentType: "audio/wav")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                .WithSummary("Render one try-out sentence { itemId, sentenceStart, draft?, asWritten, voice }: the sentence of the item starting at " +
                             "sentenceStart (an impact match's), carried on over a match that runs past its end. The list, with draft { id?, writtenForm, " +
                             "spokenForm, delete? } in place of its entry, is applied unless asWritten. voice is { voiceId } or \"item\" (the item's own " +
                             "resolved voice, also when left out); the active TTS config and the item's delivery cue are used as for its take. Answers the " +
                             "WAV body (audio/wav); nothing is stored and no item's audio changes. 400 invalid draft, no sentence starts there or a bad voice; " +
                             "404 unknown item, voice or entry; 409 while attribution, audio generation or a voice batch is queued or running anywhere " +
                             "(it holds the GPU); 422 no voice resolves, the voice has no audio or no TTS config is active; 503 TTS busy or down.");
        }

        private static async Task<IResult> ListAsync(string folder, IFileSystem fs, ProjectDbSession session, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            return Results.Ok(await PronunciationList.ReadAsync(await session.OpenAsync(folderId), ct));
        }

        private static async Task<IResult> MatchesAsync(
            string folder, Guid[]? chapterIds, IFileSystem fs, ProjectDbSession session, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            return Results.Ok(await PronunciationSpans.ReadAsync(await session.OpenAsync(folderId), chapterIds ?? [], ct));
        }

        private static async Task<IResult> AddAsync(
            string folder, SetPronunciationRequest? body, HttpRequest request, IFileSystem fs, BookMutations mutations,
            MutationOrigin origin, HistoryStepScope steps, ProjectDbSession session, AudioQueueService audioQueue, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var draft = new PronunciationDraft(null, body?.WrittenForm, body?.SpokenForm);
            var label = $"Pronunciation: add \"{PronunciationForms.Normalise(body?.WrittenForm)}\"";
            return await SetAsync(folderId, draft, label, StepAction.Pronunciation, request, mutations, origin, steps, session, audioQueue, ct) switch
            {
                (BookMutationOutcome.Committed { Receipt.Effects.CreatedId: { } id }, var discarded) =>
                    Results.Created($"/api/projects/{folder}/pronunciations/{id}", await SavedAsync(session, folderId, id, discarded, ct)),
                (var refused, _) => Refusal(refused, ct),
            };
        }

        private static async Task<IResult> EditAsync(
            string folder, Guid id, SetPronunciationRequest? body, HttpRequest request, IFileSystem fs, BookMutations mutations,
            MutationOrigin origin, HistoryStepScope steps, ProjectDbSession session, AudioQueueService audioQueue, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var draft = new PronunciationDraft(id, body?.WrittenForm, body?.SpokenForm);
            var label = $"Pronunciation: edit \"{PronunciationForms.Normalise(body?.WrittenForm)}\"";
            return await SetAsync(folderId, draft, label, StepAction.Pronunciation, request, mutations, origin, steps, session, audioQueue, ct) switch
            {
                (BookMutationOutcome.Committed or BookMutationOutcome.NoChange, var discarded) =>
                    Results.Ok(await SavedAsync(session, folderId, id, discarded, ct)),
                (var refused, _) => Refusal(refused, ct),
            };
        }

        private static async Task<IResult> DeleteAsync(
            string folder, Guid id, HttpRequest request, IFileSystem fs, BookMutations mutations,
            MutationOrigin origin, HistoryStepScope steps, ProjectDbSession session, AudioQueueService audioQueue, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            // Read for the label only: the mutation finds the entry again under the write lock.
            var written = await (await session.OpenAsync(folderId)).Pronunciations.AsNoTracking()
                .Where(p => p.Id == id).Select(p => p.WrittenForm).SingleOrDefaultAsync(ct);
            if (written is null)
                return Results.Problem($"No pronunciation {id}.", statusCode: StatusCodes.Status404NotFound);

            var draft = new PronunciationDraft(id, null, null, Delete: true);
            return await SetAsync(folderId, draft, $"Pronunciation: delete \"{written}\"", StepAction.Delete,
                    request, mutations, origin, steps, session, audioQueue, ct) switch
            {
                (BookMutationOutcome.Committed, var discarded) =>
                    Results.Ok(new PronunciationDeleted(discarded, await NeedsAudioCountAsync(session, folderId, ct))),
                (var refused, _) => Refusal(refused, ct),
            };
        }

        private static async Task<IResult> ImpactAsync(
            string folder, PronunciationDraftRequest? body, IFileSystem fs, ProjectDbSession session, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var draft = new PronunciationDraft(body?.Id, body?.WrittenForm, body?.SpokenForm, body?.Delete ?? false);
            try
            {
                return Results.Ok(await PronunciationImpact.ReadAsync(await session.OpenAsync(folderId), draft, ct));
            }
            catch (BookMutationRejectedException refused)
            {
                return Refusal(new BookMutationOutcome.Rejected(refused.Reason, refused.Message), ct);
            }
        }

        private static async Task<IResult> TryAsync(
            string folder, TryPronunciationRequest? body, HttpResponse response, IFileSystem fs, PronunciationTryOut tryOut,
            CharacterQueueService characterQueue, AudioQueueService audioQueue, VoiceBatchRunner voiceBatch, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (body is null)
                return Results.Problem("A try-out names an item and a sentence.", statusCode: StatusCodes.Status400BadRequest);
            if (GpuBusyReason(characterQueue, audioQueue, voiceBatch) is { } busy)
                return Results.Problem(busy, statusCode: StatusCodes.Status409Conflict);
            if (!TryReadVoice(body.Voice, out var voiceId))
                return Results.Problem("voice is { \"voiceId\": \"…\" } or \"item\".", statusCode: StatusCodes.Status400BadRequest);

            var draft = body.Draft is { } d ? new PronunciationDraft(d.Id, d.WrittenForm, d.SpokenForm, d.Delete) : null;
            TryOutResult result;
            try
            {
                result = await tryOut.RenderAsync(
                    folderId, new PronunciationTryRequest(body.ItemId, body.SentenceStart, draft, body.AsWritten, voiceId), ct);
            }
            catch (TtsBusyException)
            {
                return Results.Problem(TtsBusyException.UserMessage, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (AiServiceUnavailableException unavailable)
            {
                return Results.Problem(unavailable.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            switch (result)
            {
                case TryOutResult.Audio audio:
                    response.Headers.CacheControl = "no-store";
                    return Results.Bytes(audio.Wav, "audio/wav");
                case TryOutResult.Refused refused:
                    return Results.Problem(refused.Message, statusCode: refused.Reason switch
                    {
                        TryOutRefusal.NotFound => StatusCodes.Status404NotFound,
                        TryOutRefusal.CannotSpeak => StatusCodes.Status422UnprocessableEntity,
                        _ => StatusCodes.Status400BadRequest,
                    });
                default:
                    throw new NotSupportedException($"Unhandled try-out result {result.GetType().Name}.");
            }
        }

        /// <summary>
        /// Why a try-out can't play now, or null: TTS shares the one GPU with attribution's LLM, and audio
        /// generation or a voice batch would queue behind it, so any of them queued or running in any
        /// project refuses. The web words its disabled play buttons the same way.
        /// </summary>
        internal static string? GpuBusyReason(
            CharacterQueueService characterQueue, AudioQueueService audioQueue, VoiceBatchRunner voiceBatch)
        {
            var audio = audioQueue.Snapshot();
            var holder = characterQueue.Snapshot().IsBusy ? "Attribution"
                : audio.QueuedCount + audio.ProcessingCount > 0 ? "Audio generation"
                : voiceBatch.IsRunning ? "a voice batch"
                : null;
            return holder is null ? null : $"Can't play while {holder} is running: it has the GPU.";
        }

        /// <summary><c>{ "voiceId": "…" }</c> names a voice; <c>"item"</c>, or no voice at all, is the item's own (null).</summary>
        private static bool TryReadVoice(JsonElement? voice, out Guid? voiceId)
        {
            voiceId = null;
            switch (voice)
            {
                case null or { ValueKind: JsonValueKind.Null }:
                    return true;
                case { ValueKind: JsonValueKind.String } named:
                    return named.GetString() == "item";
                case { ValueKind: JsonValueKind.Object } chosen
                    when chosen.TryGetProperty("voiceId", out var id) && id.TryGetGuid(out var guid):
                    voiceId = guid;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Commits the change as one History step, unless audio generation is queued or running for the
        /// project: the recorder doesn't check a take's text is still current, so a take in flight would
        /// land with the old Spoken text. Answers the outcome and how many items lost their audio.
        /// </summary>
        private static async Task<(BookMutationOutcome Outcome, int AudioDiscardCount)> SetAsync(
            ProjectFolderId folderId, PronunciationDraft draft, string label, StepAction action, HttpRequest request,
            BookMutations mutations, MutationOrigin origin, HistoryStepScope steps, ProjectDbSession session,
            AudioQueueService audioQueue, CancellationToken ct)
        {
            if (audioQueue.BusyFor(folderId) is not null)
                return (new BookMutationOutcome.Rejected(BookMutationRejection.Conflict,
                    "Audio generation is running: wait for it or cancel it."), 0);

            OriginHeader.Apply(request, origin);
            BookMutationOutcome outcome;
            // A refused or unchanged save commits nothing, so its step has no chunk and is dropped.
            await using (steps.Open(folderId, label, action))
                outcome = await mutations.CommitAsync(new SetPronunciationMutation(folderId, draft), ct);
            return (outcome, outcome is BookMutationOutcome.Committed committed ? committed.Receipt.Effects.ParagraphItemIds.Count : 0);
        }

        private static async Task<PronunciationSaved> SavedAsync(
            ProjectDbSession session, ProjectFolderId folderId, Guid id, int audioDiscardCount, CancellationToken ct)
        {
            var entry = (await PronunciationList.ReadAsync(await session.OpenAsync(folderId), ct)).Single(e => e.Id == id);
            return new PronunciationSaved(entry.Id, entry.WrittenForm, entry.SpokenForm, entry.MatchCount, entry.ItemCount,
                entry.WithAudioCount, audioDiscardCount, await NeedsAudioCountAsync(session, folderId, ct));
        }

        /// <summary>Spoken items without audio, as a Restore counts them: what needs generating.</summary>
        private static async Task<int> NeedsAudioCountAsync(ProjectDbSession session, ProjectFolderId folderId, CancellationToken ct) =>
            await (await session.OpenAsync(folderId)).ParagraphItems.AsNoTracking()
                .CountAsync(i => i.ItemType == ParagraphItemType.Speech && (i.AudioFileName == null || i.AudioFileName == ""), ct);

        private static ProblemHttpResult Refusal(BookMutationOutcome outcome, CancellationToken ct) => outcome switch
        {
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled =>
                throw new OperationCanceledException(cancelled.Message, ct),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Validation } invalid =>
                TypedResults.Problem(invalid.Message, statusCode: StatusCodes.Status400BadRequest),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.NotFound } missing =>
                TypedResults.Problem(missing.Message, statusCode: StatusCodes.Status404NotFound),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Conflict or BookMutationRejection.Restoring } conflict =>
                TypedResults.Problem(conflict.Message, statusCode: StatusCodes.Status409Conflict),
            BookMutationOutcome.Rejected rejected =>
                TypedResults.Problem(rejected.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}."),
        };
    }
}
