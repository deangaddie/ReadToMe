using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Live;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Llm;

namespace Read2Me.App.Api
{
    public sealed record LlmModelsResponse(IReadOnlyList<string> Models);

    /// <param name="ConnectionId">The live hub connection that receives the run's <c>llmTest</c> message.</param>
    public sealed record LlmTestRequest(string? Prompt = null, string? ConnectionId = null);
    public sealed record LlmTestStatusResponse(bool Running, int? ConfigId);

    /// <param name="PromptStyle">Null stores no style: the rung inherits its config's own.</param>
    public sealed record AttributionChainStepDto(int ConfigId, bool Thinking, AttributionPromptStyle? PromptStyle = null);
    public sealed record AttributionChainRequest(IReadOnlyList<AttributionChainStepDto>? Steps = null, bool SelfConsistency = false);

    /// <param name="PromptStyle">The <em>effective</em> style — the rung's own when it set one, else the config's.</param>
    public sealed record ResolvedChainStepDto(LlmServerConfig Config, bool Thinking, AttributionPromptStyle PromptStyle);
    public sealed record AttributionChainResponse(
        IReadOnlyList<AttributionChainStepDto> Steps,
        bool SelfConsistency,
        IReadOnlyList<ResolvedChainStepDto> Resolved,
        IReadOnlyList<LlmServerConfig> Available);

    /// <summary>The LLM settings page's endpoints beyond the generic config area (Angular ticket 21).</summary>
    public static class LlmSettingsEndpoints
    {
        public static void MapLlmSettingsEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/settings/llm");

            group.MapPost("/models", GetModelsAsync)
                .WithSummary("Model ids the server behind a config offers. The body is an LLM config and need not be saved. 422 with the reason when the server cannot be asked.");

            group.MapPost("/{id:int}/test", StartTestAsync)
                .WithSummary("Send a free-text test prompt to one config (202). Tokens stream on /hubs/live group stream:llm, wrapped in runStarted/runEnded; llmTest { kind: done | failed | cancelled } goes to the connectionId in the body. 409 while a test is already running.");
            // One test runs at a time, so the id only addresses the route: whatever is running stops.
            group.MapPost("/{id:int}/test/cancel", (int id, LlmTestRunCoordinator runs) =>
                {
                    runs.Cancel();
                    return Results.Ok();
                })
                .WithSummary("Stop the running test send (one runs at a time, whichever config it targets). Harmless when nothing is running.");
            group.MapGet("/test", (LlmTestRunCoordinator runs) =>
                    Results.Ok(new LlmTestStatusResponse(runs.RunningConfigId is not null, runs.RunningConfigId)))
                .WithSummary("Whether a test send is in flight, for a client that missed its llmTest message.");

            group.MapGet("/attribution-chain", GetChainAsync)
                .WithSummary("The attribution escalation chain: stored steps in order, the self-consistency flag, the chain as attribution resolves it (an empty chain falls back to the default config) and every config a step may name.");
            group.MapPut("/attribution-chain", SetChainAsync)
                .WithSummary("Replace the attribution chain and the self-consistency flag. Exact duplicate steps collapse. 422 when a step names no config.");
        }

        private static async Task<IResult> GetModelsAsync(LlmServerConfig config, ILlmClient client, CancellationToken ct)
        {
            try
            {
                return Results.Ok(new LlmModelsResponse(await client.GetModelsAsync(config, ct)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        private static async Task<IResult> StartTestAsync(
            int id, LlmTestRequest request, LlmSettingsService settings, LlmTestRunCoordinator runs)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.Problem("A prompt is required.", statusCode: StatusCodes.Status400BadRequest);
            if ((await settings.GetAllConfigsAsync()).FirstOrDefault(c => c.Id == id) is not { } config)
                return Results.NotFound();

            return runs.TryStart(config, request.Prompt, request.ConnectionId)
                ? Results.Accepted()
                : Results.Problem("A test send is already running.", statusCode: StatusCodes.Status409Conflict);
        }

        private static async Task<IResult> GetChainAsync(LlmSettingsService settings) =>
            Results.Ok(await ReadChainAsync(settings));

        private static async Task<IResult> SetChainAsync(AttributionChainRequest request, LlmSettingsService settings)
        {
            var steps = request.Steps ?? [];
            var known = (await settings.GetAllConfigsAsync()).Select(c => c.Id).ToHashSet();
            if (steps.FirstOrDefault(s => !known.Contains(s.ConfigId)) is { } orphan)
                return Results.Problem($"No LLM config has id {orphan.ConfigId}.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            // The walk dedupes on the exact triple anyway, so a stored duplicate would be a row the
            // chain never runs.
            var entries = steps
                .Select(s => new AttributionChainEntry(s.ConfigId, s.Thinking, s.PromptStyle))
                .Distinct()
                .ToList();
            await settings.SetAttributionChainEntriesAsync(entries);
            if (await settings.GetSelfConsistencyAsync() != request.SelfConsistency)
                await settings.SetSelfConsistencyAsync(request.SelfConsistency);
            return Results.Ok(await ReadChainAsync(settings));
        }

        private static async Task<AttributionChainResponse> ReadChainAsync(LlmSettingsService settings)
        {
            var entries = await settings.GetAttributionChainEntriesAsync();
            var resolved = await settings.GetAttributionChainAsync();
            return new AttributionChainResponse(
                [.. entries.Select(e => new AttributionChainStepDto(e.ConfigId, e.Thinking, e.Style))],
                await settings.GetSelfConsistencyAsync(),
                [.. resolved.Select(s => new ResolvedChainStepDto(s.Config, s.Thinking, s.Style))],
                await settings.GetAllConfigsAsync());
        }
    }
}
