using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Characters;
using Read2Me.App.Services;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Services.Mutations;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.App.Api
{
    /// <summary>
    /// A voice as the cast page shows it. <see cref="IsEdited"/> is the on-disk invariant
    /// (<see cref="IVoiceOriginalStore.Exists"/>): the audio has been through the voice editor, so
    /// fresh audio would discard that edit. The two override JSONs are sparse patches over the
    /// active provider's settings, keyed as the provider's settings schema lists them.
    /// </summary>
    public sealed record VoiceDto(
        Guid Id, Guid CharacterId, string Name, string? Description, string Source,
        string? DesignPrompt, string? Transcript, string? AudioFileName, bool IsEdited,
        string? VoiceDesignSettingsOverrideJson, string? TtsSettingsOverrideJson);
    public sealed record CharacterVoicesDto(Guid? DefaultVoiceId, IReadOnlyList<VoiceDto> Voices);
    public sealed record VoiceBatchStartRequest(bool RegenerateAll = false);
    public sealed record VoiceBatchStatusDto(
        bool IsRunning, int Processed, int Total, int Failed,
        string? CurrentVoiceName, string? CurrentOperation, string? LastError);
    public sealed record GenerateVoiceAudioResponse(string AudioFileName, string Transcript);
    public sealed record TranscribeVoiceResponse(string Transcript);
    public sealed record RenderedDesignPromptResponse(string Prompt);
    public sealed record GenerateDesignPromptRequest(string Prompt);
    public sealed record GenerateDesignPromptResponse(string DesignPrompt);

    public static class VoiceEndpoints
    {
        /// <summary>Blazor's upload cap (<c>OpenReadStream(maxAllowedSize: 200 MB)</c>).</summary>
        public const long MaxAudioBytes = 200L * 1024 * 1024;

        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".webm",
        };

        public static void MapVoiceEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/characters/{characterId:guid}/voices", GetVoicesAsync)
                .WithSummary("A character's voices and its default voice id.");
            endpoints.MapGet("/api/projects/{folder}/voices/{voiceId:guid}", GetVoiceAsync)
                .WithSummary("One voice by id, with isEdited (its audio has been through the voice editor) and both settings overrides.");
            endpoints.MapPut("/api/projects/{folder}/voices/{voiceId:guid}/audio", UploadAudioAsync)
                .DisableAntiforgery()
                .WithSummary("Upload or replace a voice's reference audio: multipart field 'file' (audio, 200 MB max). Normalises, stores and commits in one step; answers the updated voice. Any earlier voice-editor edit is discarded.");
            endpoints.MapPost("/api/projects/{folder}/voices/{voiceId:guid}/transcribe", TranscribeAsync)
                .WithSummary("Transcribe a voice's reference audio with the active transcription service and store the result as its transcript. 422 without audio or an active service.");
            endpoints.MapPost("/api/projects/{folder}/characters/{characterId:guid}/design-prompt/render", RenderDesignPromptAsync)
                .WithSummary("The voice-design prompt template rendered for this character (book, author, name). Nothing is persisted; edit it and send it to /generate.");
            endpoints.MapPost("/api/projects/{folder}/characters/{characterId:guid}/design-prompt/generate", GenerateDesignPromptAsync)
                .WithSummary("Ask the LLM for a voice design prompt from a rendered prompt. Synchronous; publishes the LLM run on the live hub. Nothing is persisted — set it on a voice with SetVoiceDesignPrompt.");
            endpoints.MapPost("/api/projects/{folder}/characters/{characterId:guid}/voices/{voiceId:guid}/generate-audio", GenerateAudioAsync)
                .WithSummary("Synthesise reference audio for one generated voice from its design prompt. Synchronous; takes tens of seconds.");
            endpoints.MapPost("/api/projects/{folder}/voice-batch/prompts", StartPromptBatch)
                .WithSummary("Start the voice-plan batch: one LLM call per character without voices (regenerateAll replans every character). Poll /api/voice-batch/status.");
            endpoints.MapPost("/api/projects/{folder}/voice-batch/audio", StartAudioBatch)
                .WithSummary("Start the voice-audio batch: synthesise every generated voice that has a design prompt but no audio. Poll /api/voice-batch/status.");
            endpoints.MapGet("/api/voice-batch/status",
                    (VoiceBatchRunner runner) => Results.Ok(new VoiceBatchStatusDto(
                        runner.IsRunning, runner.Processed, runner.Total, runner.Failed,
                        runner.CurrentVoiceName, runner.CurrentOperation, runner.LastError)))
                .WithSummary("Voice batch progress.");
            endpoints.MapPost("/api/voice-batch/cancel",
                    (VoiceBatchRunner runner) => { runner.Cancel(); return Results.Ok(); })
                .WithSummary("Cancel the running voice batch.");
        }

        private static VoiceDto ToDto(ProjectFolderId folderId, VoiceEntity v, IVoiceOriginalStore originals) => new(
            v.Id, v.CharacterId, v.Name, v.Description, v.Source.ToString(),
            v.DesignPrompt, v.Transcript, v.AudioFileName,
            originals.Exists(folderId, v.CharacterId, v.Id),
            v.VoiceDesignSettingsOverrideJson, v.TtsSettingsOverrideJson);

        private static async Task<IResult> GetVoicesAsync(
            string folder, Guid characterId, IFileSystem fs, ICharacterReader reader, IVoiceOriginalStore originals)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voices = await reader.GetCharacterVoicesAsync(folderId, characterId);
            var defaultVoiceId = await reader.GetDefaultVoiceIdAsync(folderId, characterId);
            return Results.Ok(new CharacterVoicesDto(
                defaultVoiceId,
                voices.Select(v => ToDto(folderId, v, originals)).ToList()));
        }

        private static async Task<IResult> GetVoiceAsync(
            string folder, Guid voiceId, IFileSystem fs, ICharacterReader reader, IVoiceOriginalStore originals)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            return voice is null ? Results.NotFound() : Results.Ok(ToDto(folderId, voice, originals));
        }

        /// Mirrors CharacterPresenter.UploadVoiceAudioAsync: the orchestrator stores the recording
        /// and commits the Book mutation that names it together (ADR 0007).
        private static async Task<IResult> UploadAudioAsync(
            string folder, Guid voiceId, HttpRequest request, IFileSystem fs, ICharacterReader reader,
            IVoiceOriginalStore originals, VoiceOrchestrator orchestrator, MutationOrigin origin, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            if (voice is null)
                return Results.NotFound();

            // Kestrel's default body cap is 30 MB; the Blazor upload allows 200 MB.
            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = MaxAudioBytes;

            var (upload, refusal) = await UploadedFile.ReadAsync(
                request, AudioExtensions, MaxAudioBytes, "Voice audio must be 200 MB or smaller.", ct);
            if (upload is null)
                return refusal!;

            var character = (await reader.GetCharactersWithAliasesAsync(folderId))
                .SingleOrDefault(c => c.Id == voice.CharacterId);

            OriginHeader.Apply(request, origin);
            try
            {
                await using var stream = upload.File.OpenReadStream();
                await orchestrator.RecordUploadedAudioAsync(new AudioStoreRequest
                {
                    FolderId = folderId,
                    CharacterId = voice.CharacterId,
                    CharacterName = character?.Name ?? string.Empty,
                    CharacterAliases = character?.Aliases?.Select(a => a.Name).ToList() ?? [],
                    VoiceId = voice.Id,
                    VoiceName = voice.Name,
                    Source = stream,
                    Extension = upload.Extension,
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var updated = await reader.GetVoiceAsync(folderId, voiceId);
            return updated is null ? Results.NotFound() : Results.Ok(ToDto(folderId, updated, originals));
        }

        /// Mirrors CharacterPresenter.TranscribeVoiceAsync: transcribe, then commit the transcript.
        private static async Task<IResult> TranscribeAsync(
            string folder, Guid voiceId, HttpRequest request, IFileSystem fs, ICharacterReader reader,
            VoiceOrchestrator orchestrator, BookMutations mutations, MutationOrigin origin, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voice = await reader.GetVoiceAsync(folderId, voiceId);
            if (voice is null)
                return Results.NotFound();
            if (voice.AudioFileName is null)
                return Results.Problem("Voice has no reference audio to transcribe.", statusCode: StatusCodes.Status422UnprocessableEntity);

            string transcript;
            try
            {
                using var stream = orchestrator.OpenAudioStream(folderId, voice.AudioFileName);
                if (stream is null)
                    return Results.Problem("Could not open the voice audio file.", statusCode: StatusCodes.Status422UnprocessableEntity);
                transcript = await orchestrator.TranscribeAsync(
                    folderId, voice.Id, stream, Path.GetFileName(voice.AudioFileName), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            OriginHeader.Apply(request, origin);
            var outcome = await mutations.CommitAsync(new SetVoiceTranscriptMutation(folderId, voice.Id, transcript), ct);
            return outcome is BookMutationOutcome.Rejected rejected
                ? Results.Problem(rejected.Message, statusCode: StatusCodes.Status422UnprocessableEntity)
                : Results.Ok(new TranscribeVoiceResponse(transcript));
        }

        /// Mirrors CharacterPresenter.BuildDesignPromptAsync.
        private static async Task<IResult> RenderDesignPromptAsync(
            string folder, Guid characterId, IFileSystem fs, ICharacterReader reader,
            IProjectCatalogReader catalog, VoiceOrchestrator orchestrator)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var character = (await reader.GetCharactersWithAliasesAsync(folderId))
                .SingleOrDefault(c => c.Id == characterId);
            if (character is null)
                return Results.NotFound();

            var project = await catalog.GetProjectAsync(folderId);
            var prompt = await orchestrator.BuildRenderedPromptAsync(
                project?.BookTitle ?? string.Empty, project?.Author ?? string.Empty, character.Name);
            return Results.Ok(new RenderedDesignPromptResponse(prompt));
        }

        /// Mirrors CharacterPresenter.GenerateDesignPromptWithTextAsync: a single voice design is a
        /// Throughput Run of one, bracketed so the live hub's LLM stream shows it as a run.
        private static async Task<IResult> GenerateDesignPromptAsync(
            string folder, Guid characterId, GenerateDesignPromptRequest? body, IFileSystem fs,
            ICharacterReader reader, VoiceOrchestrator orchestrator,
            EventBroadcaster<LlmStreamEvent> llmEvents, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body?.Prompt))
                return Results.Problem("Field 'prompt' is required.", statusCode: StatusCodes.Status400BadRequest);

            if ((await reader.GetCharactersWithAliasesAsync(folderId)).All(c => c.Id != characterId))
                return Results.NotFound();

            llmEvents.Publish(new RunStarted());
            try
            {
                var designPrompt = await orchestrator.GenerateWithPromptAsync(body.Prompt, ct);
                return Results.Ok(new GenerateDesignPromptResponse(designPrompt));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            finally
            {
                llmEvents.Publish(new RunEnded());
            }
        }

        /// Mirrors GenerateAudioPhase.RunStepAsync for a single voice; the generator
        /// persists the result (SetVoiceGeneratedCommand) itself.
        private static async Task<IResult> GenerateAudioAsync(
            string folder, Guid characterId, Guid voiceId, IFileSystem fs,
            ICharacterReader reader, VoiceOrchestrator orchestrator, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var character = (await reader.GetCharactersWithAliasesAsync(folderId))
                .SingleOrDefault(c => c.Id == characterId);
            if (character is null)
                return Results.NotFound();

            var voice = (await reader.GetCharacterVoicesAsync(folderId, characterId))
                .SingleOrDefault(v => v.Id == voiceId);
            if (voice is null)
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(voice.DesignPrompt))
                return Results.Problem("Voice has no design prompt. Set one first (SetVoiceDesignPrompt command or the prompt batch).",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var result = await orchestrator.GenerateVoiceAudioAsync(new VoiceGenerationRequest
            {
                FolderId = folderId,
                CharacterId = character.Id,
                CharacterName = character.Name,
                CharacterAliases = character.Aliases?.Select(a => a.Name).ToList() ?? [],
                VoiceId = voice.Id,
                VoiceName = voice.Name,
                DesignPrompt = voice.DesignPrompt!,
                SettingsOverrideJson = voice.VoiceDesignSettingsOverrideJson,
            }, ct);

            return result.IsSuccess
                ? Results.Ok(new GenerateVoiceAudioResponse(result.AudioFileName!, result.Transcript ?? string.Empty))
                : Results.Problem(result.ErrorMessage ?? "Voice audio generation failed.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static IResult StartPromptBatch(
            string folder, VoiceBatchStartRequest? body, IFileSystem fs, VoiceBatchRunner runner)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return runner.StartGeneratePrompts(folderId, body?.RegenerateAll ?? false)
                ? Results.Accepted(value: new { started = true })
                : Results.Problem("A voice batch is already running.", statusCode: StatusCodes.Status409Conflict);
        }

        private static IResult StartAudioBatch(string folder, IFileSystem fs, VoiceBatchRunner runner)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return runner.StartGenerateAudio(folderId)
                ? Results.Accepted(value: new { started = true })
                : Results.Problem("A voice batch is already running.", statusCode: StatusCodes.Status409Conflict);
        }
    }
}
