using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Events;

namespace Read2Me.Services.Health;

/// <summary>
/// <see cref="IAiServiceControl"/> that tells the world what it saw: every probe and every
/// lifecycle op publishes a <see cref="ServiceStatusChanged"/>. The HTTP API and the pre-flight
/// runner go through this one so every live client's status chips follow; the underlying facade
/// (or its test fake) stays oblivious.
/// </summary>
public sealed class ObservedAiServiceControl(
    IAiServiceControl inner,
    EventBroadcaster<ServiceStatusChanged> events) : IAiServiceControl
{
    public DockerAiService? Resolve(string baseUrl) => inner.Resolve(baseUrl);

    public async Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct)
    {
        var status = await inner.GetStatusAsync(service, ct);
        events.Publish(new ServiceStatusChanged(service.Name, status));
        return status;
    }

    public Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct) =>
        ObserveAsync("start", service, inner.StartAsync(service, ct));

    public Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct) =>
        ObserveAsync("restart", service, inner.RestartAsync(service, ct));

    public Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct) =>
        ObserveAsync("shutdown", service, inner.ShutdownAsync(service, ct));

    private async Task<AiServiceOpResult> ObserveAsync(string op, DockerAiService service, Task<AiServiceOpResult> pending)
    {
        var result = await pending;
        events.Publish(new ServiceStatusChanged(service.Name, result.Status, op, result.Succeeded, result.Error));
        return result;
    }
}
