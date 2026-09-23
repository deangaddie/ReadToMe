using System.Collections.Concurrent;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// In-memory <see cref="IAiServiceControl"/> for E2E: no docker, no HTTP. Treats every non-empty
/// base URL as a managed container (named after the URL) so the settings-page controls render, and
/// flips its status the way the real facade would (start/restart → Ready, shutdown → Stopped).
/// <para>
/// One status for everything by default (<see cref="Status"/>); a test that needs services to
/// differ scripts <see cref="StatusByName"/> (registry names such as <c>chatterbox</c>, or a base
/// URL for a resolved config). URLs in <see cref="GpuUrls"/> resolve to GPU services, which makes
/// pre-flight sweep the registry's GPU containers for conflicts. Every op lands in
/// <see cref="OpLog"/> as <c>start:name</c>. The fixture is shared, so a test resets what it set.
/// </para>
/// </summary>
public sealed class FakeAiServiceControl : IAiServiceControl
{
    public AiServiceStatus Status { get; set; } = AiServiceStatus.Ready;
    public ConcurrentDictionary<string, AiServiceStatus> StatusByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GpuUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> OpLog { get; } = new();

    /// <summary>When set, a start of this service fails with the given error instead of going Ready.</summary>
    public ConcurrentDictionary<string, string> StartFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        Status = AiServiceStatus.Ready;
        StatusByName.Clear();
        GpuUrls.Clear();
        OpLog.Clear();
        StartFailures.Clear();
    }

    public DockerAiService? Resolve(string baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : new DockerAiService(baseUrl, "read2me-fake", baseUrl, "/health", UsesGpu: GpuUrls.Contains(baseUrl));

    public Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct) =>
        Task.FromResult(StatusByName.TryGetValue(service.Name, out var status) ? status : Status);

    public Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct) =>
        BringUpAsync("start", service);

    public Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct) =>
        BringUpAsync("restart", service);

    public Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct)
    {
        OpLog.Enqueue($"shutdown:{service.Name}");
        return Task.FromResult(new AiServiceOpResult(true, Set(service, AiServiceStatus.Stopped), null));
    }

    private Task<AiServiceOpResult> BringUpAsync(string op, DockerAiService service)
    {
        OpLog.Enqueue($"{op}:{service.Name}");
        if (StartFailures.TryGetValue(service.Name, out var error))
            return Task.FromResult(new AiServiceOpResult(false, Set(service, AiServiceStatus.Down), error));
        return Task.FromResult(new AiServiceOpResult(true, Set(service, AiServiceStatus.Ready), null));
    }

    /// <summary>A scripted service keeps its own status; everything else shares <see cref="Status"/>.</summary>
    private AiServiceStatus Set(DockerAiService service, AiServiceStatus status)
    {
        if (StatusByName.ContainsKey(service.Name))
            StatusByName[service.Name] = status;
        else
            Status = status;
        return status;
    }
}
