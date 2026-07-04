using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Health;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Scriptable <see cref="IAiServiceControl"/> for presenter/component tests — no docker, no HTTP.
    /// Set the canned results, inspect the recorded calls. A <see cref="Gate"/> can hold an op mid-flight
    /// to assert the in-flight (busy) state.
    /// </summary>
    public sealed class FakeAiServiceControl : IAiServiceControl
    {
        public DockerAiService? ResolveResult { get; set; }
        public AiServiceStatus StatusResult { get; set; } = AiServiceStatus.Stopped;
        public AiServiceOpResult StartResult { get; set; } = new(true, AiServiceStatus.Ready, null);
        public AiServiceOpResult RestartResult { get; set; } = new(true, AiServiceStatus.Ready, null);
        public AiServiceOpResult ShutdownResult { get; set; } = new(true, AiServiceStatus.Stopped, null);

        /// <summary>When set, every op awaits this before returning — lets a test observe the busy state.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public List<string> ResolvedUrls { get; } = new();
        public int StatusCalls { get; private set; }
        public List<string> Ops { get; } = new();

        public DockerAiService? Resolve(string baseUrl)
        {
            ResolvedUrls.Add(baseUrl);
            return ResolveResult;
        }

        public Task<AiServiceStatus> GetStatusAsync(DockerAiService service, CancellationToken ct)
        {
            StatusCalls++;
            return Task.FromResult(StatusResult);
        }

        public async Task<AiServiceOpResult> StartAsync(DockerAiService service, CancellationToken ct)
        {
            Ops.Add("start");
            if (Gate is not null) await Gate.Task;
            return StartResult;
        }

        public async Task<AiServiceOpResult> RestartAsync(DockerAiService service, CancellationToken ct)
        {
            Ops.Add("restart");
            if (Gate is not null) await Gate.Task;
            return RestartResult;
        }

        public async Task<AiServiceOpResult> ShutdownAsync(DockerAiService service, CancellationToken ct)
        {
            Ops.Add("shutdown");
            if (Gate is not null) await Gate.Task;
            return ShutdownResult;
        }
    }
}
