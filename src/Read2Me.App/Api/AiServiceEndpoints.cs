using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Live;
using Read2Me.Services.Health;

namespace Read2Me.App.Api
{
    public sealed record AiServiceDto(string Name, string ContainerName, string BaseUrl, bool UsesGpu);
    public sealed record AiServiceStatusDto(string Name, string Status);

    /// <summary>
    /// The Docker-hosted AI services: catalog, resolution by base URL, live status, and the manual
    /// lifecycle ops (Angular ticket 25). Every probe and op here goes through
    /// <see cref="ObservedAiServiceControl"/>, so what a caller learns every live client learns too
    /// (<c>serviceStatus</c> on <c>/hubs/live</c>).
    /// </summary>
    public static class AiServiceEndpoints
    {
        public static void MapAiServiceEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/ai-services",
                    (DockerAiServiceRegistry registry) => Results.Ok(registry.All.Select(ToDto).ToList()))
                .WithSummary("Catalog of the Docker-hosted AI services the watchdog manages.");

            endpoints.MapGet("/api/ai-services/resolve",
                    (string? baseUrl, DockerAiServiceRegistry registry) =>
                        registry.TryGetByBaseUrl(baseUrl ?? "", out var s) ? Results.Ok(ToDto(s)) : Results.NotFound())
                .WithSummary("The managed service behind a config base URL (?baseUrl=, matched on scheme/host/port), 404 when the URL is not one the watchdog manages.");

            endpoints.MapGet("/api/ai-services/status", GetAllStatusAsync)
                .WithSummary("Live status of every managed service in one call (one health probe each, in parallel); each also goes out as a serviceStatus hub message.");

            endpoints.MapGet("/api/ai-services/{name}/status", GetStatusAsync)
                .WithSummary("Live status of one AI service (single health probe): NotFound, Stopped, Starting, Ready, Recovering, Down or Unknown.");

            endpoints.MapPost("/api/ai-services/{name}/start", (string name, DockerAiServiceRegistry r, AiServiceOpCoordinator ops) =>
                    StartOp(name, AiServiceOpCoordinator.Start, r, ops))
                .WithSummary("docker start → health poll → warm-up, in the background (202). The outcome arrives as serviceStatus { name, status, op: start, ok, error? } on /hubs/live. 404 unknown service, 409 while it already has an op in flight.");

            endpoints.MapPost("/api/ai-services/{name}/restart", (string name, DockerAiServiceRegistry r, AiServiceOpCoordinator ops) =>
                    StartOp(name, AiServiceOpCoordinator.Restart, r, ops))
                .WithSummary("docker restart → health poll → warm-up, in the background (202); outcome as serviceStatus { op: restart }. 404 / 409 as start.");

            endpoints.MapPost("/api/ai-services/{name}/shutdown", (string name, DockerAiServiceRegistry r, AiServiceOpCoordinator ops) =>
                    StartOp(name, AiServiceOpCoordinator.Shutdown, r, ops))
                .WithSummary("docker stop in the background (202), pausing a queue that still has work for it; outcome as serviceStatus { op: shutdown }. 404 / 409 as start.");
        }

        private static AiServiceDto ToDto(DockerAiService s) => new(s.Name, s.ContainerName, s.BaseUrl, s.UsesGpu);

        private static DockerAiService? Find(DockerAiServiceRegistry registry, string name) =>
            registry.All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        private static async Task<IResult> GetAllStatusAsync(
            DockerAiServiceRegistry registry, ObservedAiServiceControl control, CancellationToken ct)
        {
            var probes = registry.All
                .Select(async s => new AiServiceStatusDto(s.Name, (await control.GetStatusAsync(s, ct)).ToString()))
                .ToList();
            return Results.Ok(await Task.WhenAll(probes));
        }

        private static async Task<IResult> GetStatusAsync(
            string name, DockerAiServiceRegistry registry, ObservedAiServiceControl control, CancellationToken ct)
        {
            var service = Find(registry, name);
            if (service is null)
                return Results.NotFound();

            var status = await control.GetStatusAsync(service, ct);
            return Results.Ok(new AiServiceStatusDto(service.Name, status.ToString()));
        }

        private static IResult StartOp(string name, string op, DockerAiServiceRegistry registry, AiServiceOpCoordinator ops)
        {
            var service = Find(registry, name);
            if (service is null)
                return Results.NotFound();

            return ops.TryStart(service, op)
                ? Results.Accepted()
                : Results.Problem($"{service.Name} already has a {ops.InFlight.GetValueOrDefault(service.Name, "lifecycle")} in progress.",
                    statusCode: StatusCodes.Status409Conflict);
        }
    }
}
