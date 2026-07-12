using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Single wiring point for the agent-facing HTTP API. Everything lives under /api;
    /// the trailing fallback keeps unknown /api paths out of the Blazor _Host page so
    /// they 404 like an API should instead of returning the app shell.
    /// </summary>
    public static class AgentApiEndpoints
    {
        public static void MapAgentApi(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapProjectEndpoints();
            endpoints.MapBookEndpoints();
            endpoints.MapCommandEndpoints();
            endpoints.MapAttributionEndpoints();
            endpoints.MapDiscoveryEndpoints();
            endpoints.MapVoiceEndpoints();
            endpoints.MapAssemblyEndpoints();
            endpoints.MapSettingsEndpoints();
            endpoints.MapAiServiceEndpoints();
            endpoints.MapOpenApi();
            endpoints.MapAudioEndpoints();
            endpoints.MapQueueStatusEndpoints();

            endpoints.MapFallback("/api/{**path}", () => Results.NotFound());
        }
    }
}
