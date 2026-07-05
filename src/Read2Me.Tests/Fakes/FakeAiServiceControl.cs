using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Health;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Scriptable <see cref="IAiServiceControl"/> for presenter/component tests — no docker, no HTTP.
    /// Set the canned results, inspect the recorded calls. A <see cref="Gate"/> can hold an op mid-flight
    /// to assert the in-flight (busy) state. Multi-service tests (pre-flight plans) script per service
    /// name via the *ByName dictionaries; misses fall back to the single-value properties.
    /// </summary>
    public sealed class FakeAiServiceControl : IAiServiceControl
    {
        public DockerAiService? ResolveResult { get; set; }
        public AiServiceStatus StatusResult { get; set; } = AiServiceStatus.Stopped;
        public AiServiceOpResult StartResult { get; set; } = new(true, AiServiceStatus.Ready, null);
        public AiServiceOpResult RestartResult { get; set; } = new(true, AiServiceStatus.Ready, null);
        public AiServiceOpResult ShutdownResult { get; set; } = new(true, AiServiceStatus.Stopped, null);

        public Dictionary<string, DockerAiService?> ResolveByUrl { get; } = new();
        public Dictionary<string, AiServiceStatus> StatusByName { get; } = new();
        public Dictionary<string, AiServiceOpResult> StartResultByName { get; } = new();
        public Dictionary<string, AiServiceOpResult> ShutdownResultByName { get; } = new();

        /// <summary>When set, every op awaits this before returning — lets a test observe the busy state.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<string> ResolvedUrls { get; } = new();
        public int StatusCalls { get; private set; }

        /// <summary>Recorded op kinds: "start" / "restart" / "shutdown".</summary>
        public List<string> Ops { get; } = new();

        /// <summary>Recorded ops with target, "start:llama"-style — order assertions for multi-service plans.</summary>
        public List<string> OpLog { get; } = new();

        public DockerAiService? Resolve(string baseUrl)
        {
            ResolvedUrls.Add(baseUrl);
            return ResolveByUrl.TryGetValue(baseUrl, out var service) ? service : ResolveResult;
        }

        public Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct)
        {
            StatusCalls++;
            return Task.FromResult(StatusByName.TryGetValue(service.Name, out var status) ? status : StatusResult);
        }

        public async Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct)
        {
            Ops.Add("start");
            OpLog.Add($"start:{service.Name}");
            if (Gate is not null) await Gate.Task;
            return StartResultByName.TryGetValue(service.Name, out var result) ? result : StartResult;
        }

        public async Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct)
        {
            Ops.Add("restart");
            OpLog.Add($"restart:{service.Name}");
            if (Gate is not null) await Gate.Task;
            return RestartResult;
        }

        public async Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct)
        {
            Ops.Add("shutdown");
            OpLog.Add($"shutdown:{service.Name}");
            if (Gate is not null) await Gate.Task;
            return ShutdownResultByName.TryGetValue(service.Name, out var result) ? result : ShutdownResult;
        }
    }
}
