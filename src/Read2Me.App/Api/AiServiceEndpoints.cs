using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Services.Health;

namespace Read2Me.App.Api
{
    public sealed record AiServiceDto(string Name, string ContainerName, string BaseUrl, bool UsesGpu);
    public sealed record AiServiceStatusDto(string Name, string Status);

    public static class AiServiceEndpoints
    {
        public static void MapAiServiceEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/ai-services",
                    (DockerAiServiceRegistry registry) => Results.Ok(registry.All
                        .Select(s => new AiServiceDto(s.Name, s.ContainerName, s.BaseUrl, s.UsesGpu))
                        .ToList()))
                .WithSummary("Catalog of the Docker-hosted AI services the watchdog manages.");

            endpoints.MapGet("/api/ai-services/{name}/status", GetStatusAsync)
                .WithSummary("Live status of one AI service (single health probe): NotFound, Stopped, Starting, Ready, Recovering, Down or Unknown.");
        }

        private static async Task<IResult> GetStatusAsync(
            string name, DockerAiServiceRegistry registry, IAiServiceControl control, CancellationToken ct)
        {
            var service = registry.All.FirstOrDefault(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (service is null)
                return Results.NotFound();

            var status = await control.GetStatusAsync(service, ct);
            return Results.Ok(new AiServiceStatusDto(service.Name, status.ToString()));
        }
    }
}
