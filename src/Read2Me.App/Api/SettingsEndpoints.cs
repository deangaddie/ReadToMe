using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services;

namespace Read2Me.App.Api
{
    public sealed record SetActiveRequest(int Id);
    public sealed record PromptTemplateRequest(string Template);
    public sealed record AudioProcessingUpdateRequest(
        string? FfmpegPath = null,
        double? WerThreshold = null,
        int? AudioMaxAttempts = null,
        int? ChunkPauseMs = null);

    public static class SettingsEndpoints
    {
        public static void MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
        {
            MapArea(endpoints, "llm", typeof(LlmServerConfig),
                Handlers<LlmSettingsService, LlmServerConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "llm"));

            MapArea(endpoints, "paragraph-tts", typeof(ParagraphTtsServiceConfig),
                Handlers<ParagraphTtsSettingsService, ParagraphTtsServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "paragraph-tts", ProviderSettingsJson.Canonicalize));

            MapArea(endpoints, "voice-design", typeof(VoiceDesignServiceConfig),
                Handlers<VoiceDesignSettingsService, VoiceDesignServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "voice-design", ProviderSettingsJson.Canonicalize));

            MapArea(endpoints, "transcription", typeof(TranscriptionServiceConfig),
                Handlers<TranscriptionSettingsService, TranscriptionServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "transcription", ProviderSettingsJson.Canonicalize));

            MapArea(endpoints, "semantic-similarity", typeof(SemanticSimilarityServiceConfig),
                Handlers<SemanticSimilaritySettingsService, SemanticSimilarityServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "semantic-similarity", ProviderSettingsJson.Canonicalize));

            MapPromptEndpoints(endpoints);
            MapAudioProcessingEndpoints(endpoints);
            MapSchemaEndpoints(endpoints);

            // Anything else under /api/settings is not an area — 404 instead of the Blazor fallback.
            endpoints.MapFallback("/api/settings/{**rest}", () => Results.NotFound());
        }

        // ── generic config areas ─────────────────────────────────────────────
        // The route handlers stay non-generic (HttpContext only): the ASP.NET route
        // handler analyzer crashes on generic lambda handlers under warnings-as-errors,
        // so all generic work lives in this handler factory instead.

        private sealed record AreaHandlers(
            Func<HttpContext, Task<IResult>> List,
            Func<HttpContext, Task<IResult>> Create,
            Func<HttpContext, int, Task<IResult>> Update,
            Func<HttpContext, int, Task<IResult>> Delete,
            Func<HttpContext, Task<IResult>> GetActive,
            Func<HttpContext, Task<IResult>> SetActive);

        private static AreaHandlers Handlers<TService, TConfig>(
            Func<TService, Task<List<TConfig>>> getAll,
            Func<TService, Task<TConfig?>> getActive,
            Func<TService, int, Task> setActive,
            Func<TService, TConfig, Task<TConfig>> create,
            Func<TService, TConfig, Task> update,
            Func<TService, int, Task> delete,
            Func<TConfig, int> getId,
            Action<TConfig, int> setId,
            string area,
            Action<TConfig>? canonicalize = null)
            where TService : class where TConfig : class
        {
            TService Svc(HttpContext ctx) => ctx.RequestServices.GetRequiredService<TService>();

            // Null when the config is fit to store; the provider areas rewrite settingsJson on the way.
            IResult? Refusal(TConfig config)
            {
                try
                {
                    canonicalize?.Invoke(config);
                    return null;
                }
                catch (JsonException ex)
                {
                    return Results.Problem($"settingsJson is not this provider type's settings: {ex.Message}",
                        statusCode: StatusCodes.Status400BadRequest);
                }
            }

            return new AreaHandlers(
                List: async ctx => Results.Ok(await getAll(Svc(ctx))),
                Create: async ctx =>
                {
                    if (await ctx.Request.ReadFromJsonAsync<TConfig>() is not { } config)
                        return Results.Problem("Missing config body.", statusCode: StatusCodes.Status400BadRequest);
                    if (Refusal(config) is { } refusal)
                        return refusal;
                    setId(config, 0);
                    var created = await create(Svc(ctx), config);
                    return Results.Created($"/api/settings/{area}/{getId(created)}", created);
                },
                Update: async (ctx, id) =>
                {
                    if (await ctx.Request.ReadFromJsonAsync<TConfig>() is not { } config)
                        return Results.Problem("Missing config body.", statusCode: StatusCodes.Status400BadRequest);
                    var svc = Svc(ctx);
                    if ((await getAll(svc)).All(c => getId(c) != id))
                        return Results.NotFound();
                    if (Refusal(config) is { } refusal)
                        return refusal;
                    setId(config, id);
                    await update(svc, config);
                    return Results.Ok(config);
                },
                Delete: async (ctx, id) =>
                {
                    await delete(Svc(ctx), id);
                    return Results.NoContent();
                },
                GetActive: async ctx =>
                    await getActive(Svc(ctx)) is { } active ? Results.Ok(active) : Results.NotFound(),
                SetActive: async ctx =>
                {
                    if (await ctx.Request.ReadFromJsonAsync<SetActiveRequest>() is not { } request)
                        return Results.Problem("Missing body.", statusCode: StatusCodes.Status400BadRequest);
                    var svc = Svc(ctx);
                    if ((await getAll(svc)).All(c => getId(c) != request.Id))
                        return Results.NotFound();
                    await setActive(svc, request.Id);
                    return Results.Ok();
                });
        }

        private static void MapArea(IEndpointRouteBuilder endpoints, string area, Type configType, AreaHandlers h)
        {
            var group = endpoints.MapGroup($"/api/settings/{area}");

            group.MapGet("", h.List)
                .WithSummary($"List {area} configs.");
            group.MapPost("", h.Create)
                .WithMetadata(new AcceptsMetadata(["application/json"], configType, isOptional: false))
                .WithSummary($"Create a {area} config. The first config auto-activates.");
            group.MapPut("/{id:int}", h.Update)
                .WithMetadata(new AcceptsMetadata(["application/json"], configType, isOptional: false))
                .WithSummary($"Update a {area} config by id.");
            group.MapDelete("/{id:int}", h.Delete)
                .WithSummary($"Delete a {area} config. Active selection reassigns or clears.");
            group.MapGet("/active", h.GetActive)
                .WithSummary($"The active {area} config, 404 when none.");
            group.MapPut("/active", h.SetActive)
                .WithMetadata(new AcceptsMetadata(["application/json"], typeof(SetActiveRequest), isOptional: false))
                .WithSummary($"Select the active {area} config.");
        }

        // ── provider settings schemas (Angular ticket 16) ────────────────────

        private static void MapSchemaEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/paragraph-tts/schema", (string? type) =>
                    TryParseType<ParagraphTtsServiceType>(type, out var t)
                        ? Results.Ok(ProviderSettingsSchema.ParagraphTts(t))
                        : UnknownProviderType(type, typeof(ParagraphTtsServiceType)))
                .WithSummary("The editable fields of one TTS provider type (?type=VoxCpm2|Chatterbox|ChatterboxTurbo|Qwen3Base, name or number) with ranges and recommended defaults. Keys are the settingsJson property names, so a sparse object of them is a valid per-voice override.");
            endpoints.MapGet("/api/settings/voice-design/schema", (string? type) =>
                    TryParseType<VoiceDesignServiceType>(type, out var t)
                        ? Results.Ok(ProviderSettingsSchema.VoiceDesign(t))
                        : UnknownProviderType(type, typeof(VoiceDesignServiceType)))
                .WithSummary("The editable fields of one voice-design provider type (?type=VoxCpm2|Qwen3, name or number) with ranges and recommended defaults. Keys are the settingsJson property names, so a sparse object of them is a valid per-voice override.");
            endpoints.MapGet("/api/settings/transcription/schema", (string? type) =>
                    TryParseType<TranscriptionServiceType>(type, out var t)
                        ? Results.Ok(ProviderSettingsSchema.Transcription(t))
                        : UnknownProviderType(type, typeof(TranscriptionServiceType)))
                .WithSummary("The editable fields of one transcription provider type (?type=LocalWhisper, name or number) beyond its base URL — none today.");
            endpoints.MapGet("/api/settings/semantic-similarity/schema", (string? type) =>
                    TryParseType<SemanticSimilarityServiceType>(type, out var t)
                        ? Results.Ok(ProviderSettingsSchema.SemanticSimilarity(t))
                        : UnknownProviderType(type, typeof(SemanticSimilarityServiceType)))
                .WithSummary("The editable fields of one semantic-similarity provider type (?type=MiniLmL6|MpnetBaseV2, name or number) beyond its base URL: the pass threshold.");
        }

        private static bool TryParseType<TEnum>(string? raw, out TEnum type) where TEnum : struct, Enum =>
            Enum.TryParse(raw, ignoreCase: true, out type) && Enum.IsDefined(type);

        private static IResult UnknownProviderType(string? raw, Type enumType) =>
            Results.Problem(
                $"Unknown provider type '{raw}'. Expected one of: {string.Join(", ", Enum.GetNames(enumType))}.",
                statusCode: StatusCodes.Status400BadRequest);

        // ── prompts ──────────────────────────────────────────────────────────

        private sealed record PromptKind(
            Func<LlmPromptService, Task<string>> Get,
            Func<LlmPromptService, string, Task> Set,
            Func<LlmPromptService, Task> Reset);

        private static readonly Dictionary<string, PromptKind> PromptKinds = new()
        {
            ["character"] = new(s => s.GetCharacterPromptAsync(AttributionPromptStyle.Full),
                (s, t) => s.SetCharacterPromptAsync(t), s => s.ResetCharacterPromptAsync()),
            ["simple-character"] = new(s => s.GetCharacterPromptAsync(AttributionPromptStyle.Simple),
                (s, t) => s.SetSimpleCharacterPromptAsync(t), s => s.ResetSimpleCharacterPromptAsync()),
            ["batch-character"] = new(s => s.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full),
                (s, t) => s.SetBatchCharacterPromptAsync(t), s => s.ResetBatchCharacterPromptAsync()),
            ["simple-batch-character"] = new(s => s.GetBatchCharacterPromptAsync(AttributionPromptStyle.Simple),
                (s, t) => s.SetSimpleBatchCharacterPromptAsync(t), s => s.ResetSimpleBatchCharacterPromptAsync()),
            ["voice"] = new(s => s.GetVoicePromptAsync(),
                (s, t) => s.SetVoicePromptAsync(t), s => s.ResetVoicePromptAsync()),
            ["voice-plan"] = new(s => s.GetVoicePlanPromptAsync(),
                (s, t) => s.SetVoicePlanPromptAsync(t), s => s.ResetVoicePlanPromptAsync()),
            ["narrator-voice-plan"] = new(s => s.GetNarratorVoicePlanPromptAsync(),
                (s, t) => s.SetNarratorVoicePlanPromptAsync(t), s => s.ResetNarratorVoicePlanPromptAsync()),
            ["discover-characters"] = new(s => s.GetDiscoverCharactersPromptAsync(),
                (s, t) => s.SetDiscoverCharactersPromptAsync(t), s => s.ResetDiscoverCharactersPromptAsync()),
        };

        private static void MapPromptEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/prompts", GetAllPromptsAsync)
                .WithSummary("Every prompt template, resolved (stored override or built-in default), keyed by kind.");
            endpoints.MapPut("/api/settings/prompts/{kind}", SetPromptAsync)
                .WithSummary("Override one prompt template.");
            endpoints.MapDelete("/api/settings/prompts/{kind}", ResetPromptAsync)
                .WithSummary("Reset one prompt template to its built-in default.");
        }

        private static async Task<IResult> GetAllPromptsAsync(LlmPromptService svc)
        {
            var result = new Dictionary<string, string>();
            foreach (var (kind, ops) in PromptKinds)
                result[kind] = await ops.Get(svc);
            return Results.Ok(result);
        }

        private static async Task<IResult> SetPromptAsync(string kind, PromptTemplateRequest request, LlmPromptService svc)
        {
            if (!PromptKinds.TryGetValue(kind, out var ops))
                return UnknownPromptKind(kind);
            await ops.Set(svc, request.Template);
            return Results.Ok();
        }

        private static async Task<IResult> ResetPromptAsync(string kind, LlmPromptService svc)
        {
            if (!PromptKinds.TryGetValue(kind, out var ops))
                return UnknownPromptKind(kind);
            await ops.Reset(svc);
            return Results.Ok();
        }

        private static IResult UnknownPromptKind(string kind) =>
            Results.Problem($"Unknown prompt kind '{kind}'. Known kinds: {string.Join(", ", PromptKinds.Keys.OrderBy(k => k))}.",
                statusCode: StatusCodes.Status400BadRequest);

        // ── audio processing (single row) ────────────────────────────────────

        private static void MapAudioProcessingEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/audio-processing", GetAudioProcessingAsync)
                .WithSummary("Audio post-processing scalars: ffmpeg path, WER threshold, retry count, pause durations.");
            endpoints.MapPut("/api/settings/audio-processing", UpdateAudioProcessingAsync)
                .WithSummary("Update audio post-processing scalars; only supplied fields change.");
        }

        private static async Task<IResult> GetAudioProcessingAsync(AudioProcessingSettingsService svc) =>
            Results.Ok(await svc.GetAsync());

        private static async Task<IResult> UpdateAudioProcessingAsync(
            AudioProcessingUpdateRequest request, AudioProcessingSettingsService svc)
        {
            if (request.FfmpegPath is not null)
                await svc.SetFfmpegPathAsync(request.FfmpegPath);
            if (request.WerThreshold is { } wer)
                await svc.SetWerThresholdAsync(wer);
            if (request.AudioMaxAttempts is { } attempts)
                await svc.SetAudioMaxAttemptsAsync(attempts);
            if (request.ChunkPauseMs is { } chunk)
                await svc.SetChunkPauseAsync(chunk);
            return Results.Ok(await svc.GetAsync());
        }
    }
}
