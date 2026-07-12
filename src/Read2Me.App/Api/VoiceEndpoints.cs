using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Characters;
using Read2Me.App.Services;
using Read2Me.Core.IO;
using Read2Me.Services;
using Read2Me.Services.Audio.VoiceDesign;

namespace Read2Me.App.Api
{
    public sealed record VoiceDto(
        Guid Id, string Name, string? Description, string Source,
        string? DesignPrompt, string? Transcript, string? AudioFileName);
    public sealed record CharacterVoicesDto(Guid? DefaultVoiceId, IReadOnlyList<VoiceDto> Voices);
    public sealed record VoiceBatchStartRequest(bool RegenerateAll = false);
    public sealed record VoiceBatchStatusDto(
        bool IsRunning, int Processed, int Total, int Failed,
        string? CurrentVoiceName, string? CurrentOperation, string? LastError);
    public sealed record GenerateVoiceAudioResponse(string AudioFileName, string Transcript);

    public static class VoiceEndpoints
    {
        public static void MapVoiceEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/characters/{characterId:guid}/voices", GetVoicesAsync)
                .WithSummary("A character's voices and its default voice id.");
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

        private static async Task<IResult> GetVoicesAsync(
            string folder, Guid characterId, IFileSystem fs, ICharacterReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var voices = await reader.GetCharacterVoicesAsync(folderId, characterId);
            var defaultVoiceId = await reader.GetDefaultVoiceIdAsync(folderId, characterId);
            return Results.Ok(new CharacterVoicesDto(
                defaultVoiceId,
                voices.Select(v => new VoiceDto(
                    v.Id, v.Name, v.Description, v.Source.ToString(),
                    v.DesignPrompt, v.Transcript, v.AudioFileName)).ToList()));
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
