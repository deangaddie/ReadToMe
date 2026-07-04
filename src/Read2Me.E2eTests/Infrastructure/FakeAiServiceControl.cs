using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// In-memory <see cref="IAiServiceControl"/> for E2E: no docker, no HTTP. Treats every non-empty
/// base URL as a managed container so the settings-page controls render, and flips its status the
/// way the real facade would (start/restart → Ready, shutdown → Stopped).
/// </summary>
public sealed class FakeAiServiceControl : IAiServiceControl
{
    public AiServiceStatus Status { get; set; } = AiServiceStatus.Ready;

    public DockerAiService? Resolve(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : new DockerAiService(baseUrl, "read2me-fake", baseUrl, "/health");

    public Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct) =>
        Task.FromResult(Status);

    public Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct)
    {
        Status = AiServiceStatus.Ready;
        return Task.FromResult(new AiServiceOpResult(true, Status, null));
    }

    public Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct)
    {
        Status = AiServiceStatus.Ready;
        return Task.FromResult(new AiServiceOpResult(true, Status, null));
    }

    public Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct)
    {
        Status = AiServiceStatus.Stopped;
        return Task.FromResult(new AiServiceOpResult(true, Status, null));
    }
}
