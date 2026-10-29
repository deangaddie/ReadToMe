using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What the AI configs and Docker services rows read: the settings services (scoped, so one
    /// scope per check), the managed-service control behind every status chip, the container
    /// registry, HTTP for the read-only probes, and the watchdog options.
    /// </summary>
    public sealed record SetupCheckServices(
        IServiceScopeFactory Scopes,
        IAiServiceControl Control,
        DockerAiServiceRegistry Registry,
        IHttpClientFactory Http,
        IOptions<AiWatchdogOptions> Watchdog);
}
