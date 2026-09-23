using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Health
{
    public class ObservedAiServiceControlTests
    {
        private static readonly DockerAiService Llama = new("llama", "read2me-llama", "http://localhost:8080", "/health", UsesGpu: true);

        private readonly FakeAiServiceControl _inner = new();
        private readonly EventBroadcaster<ServiceStatusChanged> _events = new();
        private readonly List<ServiceStatusChanged> _published = [];
        private readonly ObservedAiServiceControl _control;

        public ObservedAiServiceControlTests()
        {
            _events.Event += _published.Add;
            _control = new ObservedAiServiceControl(_inner, _events);
        }

        [Fact]
        public async Task Probe_publishes_the_status_it_found()
        {
            _inner.StatusByName["llama"] = AiServiceStatus.Starting;

            var status = await _control.GetStatusAsync(Llama, CancellationToken.None);

            Assert.Equal(AiServiceStatus.Starting, status);
            var change = Assert.Single(_published);
            Assert.Equal(new ServiceStatusChanged("llama", AiServiceStatus.Starting), change);
        }

        [Fact]
        public async Task Ops_publish_the_resulting_status_with_the_op_and_its_outcome()
        {
            _inner.ShutdownResult = new AiServiceOpResult(true, AiServiceStatus.Stopped, null);
            _inner.StartResult = new AiServiceOpResult(false, AiServiceStatus.Down, "boom");

            await _control.ShutdownAsync(Llama, CancellationToken.None);
            var failed = await _control.StartAsync(Llama, CancellationToken.None);
            await _control.RestartAsync(Llama, CancellationToken.None);

            Assert.False(failed.Succeeded);
            Assert.Equal(["shutdown", "start", "restart"], _inner.Ops);
            Assert.Equal(
            [
                new ServiceStatusChanged("llama", AiServiceStatus.Stopped, "shutdown", true, null),
                new ServiceStatusChanged("llama", AiServiceStatus.Down, "start", false, "boom"),
                new ServiceStatusChanged("llama", AiServiceStatus.Ready, "restart", true, null),
            ], _published);
        }

        [Fact]
        public void Resolve_passes_through()
        {
            _inner.ResolveByUrl["http://localhost:8080"] = Llama;

            Assert.Same(Llama, _control.Resolve("http://localhost:8080"));
            Assert.Null(_control.Resolve("https://api.example.com"));
        }
    }
}
