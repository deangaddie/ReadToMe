using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Llm;

namespace Read2Me.App.Api
{
    public sealed record SetActiveRequest(int Id);
    public sealed record PromptTemplateRequest(string Template);
    public sealed record PromptPreviewRequest(string Template);
    public sealed record PromptPreviewResponse(string Rendered);
    /// <summary>One prompt kind as the settings pages show it; <c>Template</c> is the resolved text.</summary>
    public sealed record PromptCatalogEntry(
        string Kind,
        string Title,
        string Description,
        IReadOnlyList<string> Tokens,
        string? ExpectedResponse,
        string Template,
        string DefaultTemplate,
        bool IsOverridden,
        IReadOnlyList<string> Warnings);

    public static class SettingsEndpoints
    {
        public static void MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
        {
            MapArea(endpoints, "llm",
                Handlers<LlmSettingsService, LlmServerConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "llm"));

            MapArea(endpoints, "paragraph-tts",
                Handlers<ParagraphTtsSettingsService, ParagraphTtsServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "paragraph-tts", ProviderSettingsJson.Canonicalize));

            MapArea(endpoints, "voice-design",
                Handlers<VoiceDesignSettingsService, VoiceDesignServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "voice-design", ProviderSettingsJson.Canonicalize));

            MapArea(endpoints, "transcription",
                Handlers<TranscriptionSettingsService, TranscriptionServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "transcription", ProviderSettingsJson.Canonicalize));

            MapArea(endpoints, "semantic-similarity",
                Handlers<SemanticSimilaritySettingsService, SemanticSimilarityServiceConfig>(
                    s => s.GetAllConfigsAsync(), s => s.GetActiveConfigAsync(), (s, id) => s.SetActiveConfigAsync(id),
                    (s, c) => s.CreateConfigAsync(c), (s, c) => s.UpdateConfigAsync(c), (s, id) => s.DeleteConfigAsync(id),
                    c => c.Id, (c, id) => c.Id = id, "semantic-similarity", ProviderSettingsJson.Canonicalize));

            MapPromptEndpoints(endpoints);
            endpoints.MapAudioProcessingEndpoints();
            MapSchemaEndpoints(endpoints);

            // Anything else under /api/settings is not an area — 404, not the web app's fallback page.
            endpoints.MapFallback("/api/settings/{**rest}", () => TypedResults.NotFound());
        }

        // ── generic config areas ─────────────────────────────────────────────
        // The route handlers stay non-generic (HttpContext only): the ASP.NET route
        // handler analyzer crashes on generic lambda handlers under warnings-as-errors,
        // so all generic work lives in this handler factory instead.

        // Each delegate's return type is its declaration: closed over TConfig at run time, so the
        // document names the area's own entity.
        private sealed record AreaHandlers<TConfig>(
            Func<HttpContext, Task<Ok<List<TConfig>>>> List,
            Func<HttpContext, Task<Results<Created<TConfig>, ProblemHttpResult>>> Create,
            Func<HttpContext, int, Task<Results<Ok<TConfig>, NotFound, ProblemHttpResult>>> Update,
            Func<HttpContext, int, Task<NoContent>> Delete,
            Func<HttpContext, Task<Results<Ok<TConfig>, NotFound>>> GetActive,
            Func<HttpContext, Task<Results<NoContent, NotFound, ProblemHttpResult>>> SetActive);

        private static AreaHandlers<TConfig> Handlers<TService, TConfig>(
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
            ProblemHttpResult? Refusal(TConfig config)
            {
                try
                {
                    canonicalize?.Invoke(config);
                    return null;
                }
                catch (JsonException ex)
                {
                    return TypedResults.Problem($"settingsJson is not this provider type's settings: {ex.Message}",
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (NotSupportedException ex)
                {
                    return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            return new AreaHandlers<TConfig>(
                List: async ctx => TypedResults.Ok(await getAll(Svc(ctx))),
                Create: async Task<Results<Created<TConfig>, ProblemHttpResult>> (ctx) =>
                {
                    if ((await ApiJson.ReadBodyAsync<TConfig>(ctx.Request, "Missing config body.")).Failed(out var config, out var refused))
                        return refused;
                    if (Refusal(config) is { } refusal)
                        return refusal;
                    setId(config, 0);
                    var created = await create(Svc(ctx), config);
                    return TypedResults.Created($"/api/settings/{area}/{getId(created)}", created);
                },
                Update: async Task<Results<Ok<TConfig>, NotFound, ProblemHttpResult>> (ctx, id) =>
                {
                    if ((await ApiJson.ReadBodyAsync<TConfig>(ctx.Request, "Missing config body.")).Failed(out var config, out var refused))
                        return refused;
                    var svc = Svc(ctx);
                    if ((await getAll(svc)).All(c => getId(c) != id))
                        return TypedResults.NotFound();
                    if (Refusal(config) is { } refusal)
                        return refusal;
                    setId(config, id);
                    await update(svc, config);
                    return TypedResults.Ok(config);
                },
                Delete: async (ctx, id) =>
                {
                    await delete(Svc(ctx), id);
                    return TypedResults.NoContent();
                },
                GetActive: async Task<Results<Ok<TConfig>, NotFound>> (ctx) =>
                    await getActive(Svc(ctx)) is { } active ? TypedResults.Ok(active) : TypedResults.NotFound(),
                SetActive: async Task<Results<NoContent, NotFound, ProblemHttpResult>> (ctx) =>
                {
                    if ((await ApiJson.ReadBodyAsync<SetActiveRequest>(ctx.Request, "Missing body.")).Failed(out var request, out var refused))
                        return refused;
                    var svc = Svc(ctx);
                    if ((await getAll(svc)).All(c => getId(c) != request.Id))
                        return TypedResults.NotFound();
                    await setActive(svc, request.Id);
                    return TypedResults.NoContent();
                });
        }

        private static void MapArea<TConfig>(IEndpointRouteBuilder endpoints, string area, AreaHandlers<TConfig> h)
        {
            var configType = typeof(TConfig);
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
                .WithSummary($"Select the active {area} config. 204 on success, 404 when no config has the id.");
        }


        // ── provider settings schemas (Angular ticket 16) ────────────────────

        private static void MapSchemaEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/paragraph-tts/schema", (string? type) =>
                    SchemaFor<ParagraphTtsServiceType, ParagraphTtsSettingsSchema>(type, ProviderSettingsSchema.ParagraphTts))
                .WithSummary("The editable fields of one TTS provider type (?type=VoxCpm2|Chatterbox|Qwen3Base|Breeze, member name) with ranges and recommended defaults. Keys are the settingsJson property names, so a sparse object of them is a valid per-voice override.");
            endpoints.MapGet("/api/settings/voice-design/schema", (string? type) =>
                    SchemaFor<VoiceDesignServiceType, VoiceDesignSettingsSchema>(type, ProviderSettingsSchema.VoiceDesign))
                .WithSummary("The editable fields of one voice-design provider type (?type=VoxCpm2|Qwen3|Breeze, member name) with ranges and recommended defaults. Keys are the settingsJson property names, so a sparse object of them is a valid per-voice override.");
            endpoints.MapGet("/api/settings/transcription/schema", (string? type) =>
                    SchemaFor<TranscriptionServiceType, TranscriptionSettingsSchema>(type, ProviderSettingsSchema.Transcription))
                .WithSummary("The editable fields of one transcription provider type (?type=LocalWhisper, member name) beyond its base URL — none today.");
            endpoints.MapGet("/api/settings/semantic-similarity/schema", (string? type) =>
                    SchemaFor<SemanticSimilarityServiceType, SemanticSimilaritySettingsSchema>(type, ProviderSettingsSchema.SemanticSimilarity))
                .WithSummary("The editable fields of one semantic-similarity provider type (?type=MiniLmL6|MpnetBaseV2, member name) beyond its base URL: the pass threshold.");
        }

        /// <summary>The provider type's schema, or a 400 naming the member names when <paramref name="raw"/> is none of them.</summary>
        private static Results<Ok<TSchema>, ProblemHttpResult> SchemaFor<TType, TSchema>(string? raw, Func<TType, TSchema> build)
            where TType : struct, Enum =>
            ApiJson.TryParseName<TType>(raw, out var type)
                ? TypedResults.Ok(build(type))
                : TypedResults.Problem(
                    $"Unknown provider type '{raw}'. Expected one of: {string.Join(", ", Enum.GetNames<TType>())}.",
                    statusCode: StatusCodes.Status400BadRequest);

        // ── prompts ──────────────────────────────────────────────────────────
        // The kinds, their copy, tokens, defaults and sample values are the catalog's; the API only
        // adds the resolved template, the override flag and the compatibility warnings.

        private static void MapPromptEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/prompts", GetAllPromptsAsync)
                .WithSummary("Every prompt template, resolved (stored override or built-in default), keyed by kind.");
            endpoints.MapGet("/api/settings/prompts/catalog", GetPromptCatalogAsync)
                .WithSummary("Every prompt kind with its description, tokens, expected response, resolved and default templates, override flag and compatibility warnings.");
            endpoints.MapPost("/api/settings/prompts/{kind}/preview", PreviewPrompt)
                .WithSummary("Render a template (saved or not) with the kind's sample values.");
            endpoints.MapPut("/api/settings/prompts/{kind}", SetPromptAsync)
                .WithSummary("Override one prompt template. 204 on success.");
            endpoints.MapDelete("/api/settings/prompts/{kind}", ResetPromptAsync)
                .WithSummary("Reset one prompt template to its built-in default. 204 on success.");
        }

        private static async Task<Ok<Dictionary<string, string>>> GetAllPromptsAsync(LlmPromptService svc)
        {
            var result = new Dictionary<string, string>();
            foreach (var kind in PromptCatalog.Kinds)
                result[kind.Kind] = await kind.Get(svc);
            return TypedResults.Ok(result);
        }

        private static async Task<Ok<List<PromptCatalogEntry>>> GetPromptCatalogAsync(LlmPromptService svc)
        {
            var compatibility = await svc.GetAttributionPromptCompatibilityAsync();
            var entries = new List<PromptCatalogEntry>(PromptCatalog.Kinds.Count);
            foreach (var kind in PromptCatalog.Kinds)
            {
                var template = await kind.Get(svc);
                entries.Add(new PromptCatalogEntry(
                    kind.Kind, kind.Title, kind.Description, kind.Tokens, kind.ExpectedResponse,
                    template, kind.DefaultTemplate,
                    IsOverridden: !string.Equals(template, kind.DefaultTemplate, StringComparison.Ordinal),
                    kind.Warnings(compatibility)));
            }
            return TypedResults.Ok(entries);
        }

        private static Results<Ok<PromptPreviewResponse>, ProblemHttpResult> PreviewPrompt(string kind, PromptPreviewRequest request)
        {
            var descriptor = PromptCatalog.Find(kind);
            if (descriptor is null)
                return UnknownPromptKind(kind);
            return TypedResults.Ok(new PromptPreviewResponse(
                PromptTemplates.Render(request.Template ?? string.Empty, descriptor.SampleValues)));
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> SetPromptAsync(string kind, PromptTemplateRequest request, LlmPromptService svc)
        {
            var descriptor = PromptCatalog.Find(kind);
            if (descriptor is null)
                return UnknownPromptKind(kind);
            await descriptor.Set(svc, request.Template);
            return TypedResults.NoContent();
        }

        private static async Task<Results<NoContent, ProblemHttpResult>> ResetPromptAsync(string kind, LlmPromptService svc)
        {
            var descriptor = PromptCatalog.Find(kind);
            if (descriptor is null)
                return UnknownPromptKind(kind);
            await descriptor.Reset(svc);
            return TypedResults.NoContent();
        }

        private static ProblemHttpResult UnknownPromptKind(string kind) =>
            TypedResults.Problem($"Unknown prompt kind '{kind}'. Known kinds: {string.Join(", ", PromptCatalog.Kinds.Select(k => k.Kind).OrderBy(k => k))}.",
                statusCode: StatusCodes.Status400BadRequest);
    }
}
