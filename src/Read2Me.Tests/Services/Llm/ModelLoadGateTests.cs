using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    public class ModelLoadGateTests
    {
        private static LlmServerConfig SwitchableConfig() =>
            new() { Name = "Llama", BaseUrl = "http://localhost:8080", Model = "m", SupportsModelSwitch = true };

        private static ModelLoadGate NewGate(FakeHandler handler, AiWatchdogOptions? options = null)
        {
            var factory = new SingleHandlerFactory(handler);
            return new ModelLoadGate(
                factory,
                NullLogger<ModelLoadGate>.Instance,
                Options.Create(options ?? FastOptions()));
        }

        // Tiny budget/interval so the switch-and-poll loop runs at test speed.
        private static AiWatchdogOptions FastOptions(int budgetSeconds = 30) =>
            new() { ModelLoadBudgetSeconds = budgetSeconds, ModelLoadPollIntervalSeconds = 0 };

        [Fact]
        public async Task NonSwitchableConfig_IsNoOp_NoHttp()
        {
            var handler = new FakeHandler();
            var gate = NewGate(handler);
            var config = SwitchableConfig();
            config.SupportsModelSwitch = false;

            await gate.EnsureModelLoadedAsync(config, CancellationToken.None);

            Assert.Equal(0, handler.GetCount);
            Assert.Equal(0, handler.PostCount);
        }

        [Fact]
        public async Task AlreadyLoaded_FastPath_NoLockNoTrigger()
        {
            // Target reads loaded on the first (lock-free) detection GET → return, no switch.
            var handler = new FakeHandler { OnGet = _ => Models("loaded") };
            var gate = NewGate(handler);

            await gate.EnsureModelLoadedAsync(SwitchableConfig(), CancellationToken.None);

            Assert.Equal(1, handler.GetCount);   // single lock-free detection GET
            Assert.Equal(0, handler.PostCount);  // no autoload trigger
        }

        [Fact]
        public async Task Unloaded_SwitchesPollsUntilLoaded_ThenReturns()
        {
            // Detection + re-check under lock read unloaded; the poll then transitions to loaded.
            var statuses = new[] { "unloaded", "unloaded", "loading", "loaded" };
            var handler = new FakeHandler
            {
                OnGet = i => Models(statuses[Math.Min(i, statuses.Length - 1)]),
                OnPost = _ => Ok("{}"),
            };
            var gate = NewGate(handler);

            await gate.EnsureModelLoadedAsync(SwitchableConfig(), CancellationToken.None);

            Assert.Equal(1, handler.PostCount);   // exactly one autoload trigger
            Assert.True(handler.GetCount >= 4);
        }

        [Fact]
        public async Task TwoConcurrentCallers_Coalesce_SingleTrigger()
        {
            // GET reads unloaded until a trigger has fired, loaded thereafter. Two callers both miss the
            // lock-free path; the lock serialises them and the second finds it already loaded.
            var handler = new FakeHandler { OnPost = _ => Ok("{}") };
            handler.OnGet = _ => Models(handler.PostSeen ? "loaded" : "unloaded");
            var gate = NewGate(handler);
            var config = SwitchableConfig();

            var a = gate.EnsureModelLoadedAsync(config, CancellationToken.None);
            var b = gate.EnsureModelLoadedAsync(config, CancellationToken.None);
            await Task.WhenAll(a, b);

            Assert.Equal(1, handler.PostCount);
        }

        [Fact]
        public async Task BudgetExceeded_ThrowsModelStillLoading()
        {
            // The server stays responsive (GET keeps reading loading) but never finishes within budget.
            var handler = new FakeHandler
            {
                OnGet = _ => Models("loading"),
                OnPost = _ => Ok("{}"),
            };
            var gate = NewGate(handler, FastOptions(budgetSeconds: 0));

            var ex = await Assert.ThrowsAsync<ModelStillLoadingException>(() =>
                gate.EnsureModelLoadedAsync(SwitchableConfig(), CancellationToken.None));

            Assert.Equal("http://localhost:8080", ex.BaseUrl);
            Assert.Equal("m", ex.Model);
            Assert.Equal(TimeSpan.Zero, ex.Budget);
            Assert.Equal(1, handler.PostCount);   // triggered once before giving up
        }

        [Fact]
        public async Task HardTriggerFailure_ThrowsLlmProvider()
        {
            // A fast 400 (unknown/misconfigured model) is a genuine failure, not "busy".
            var handler = new FakeHandler
            {
                OnGet = _ => Models("loading"),
                OnPost = _ => Status(HttpStatusCode.BadRequest, "unknown model"),
            };
            var gate = NewGate(handler);

            await Assert.ThrowsAsync<LlmProviderException>(() =>
                gate.EnsureModelLoadedAsync(SwitchableConfig(), CancellationToken.None));

            Assert.Equal(1, handler.PostCount);
        }

        [Fact]
        public async Task ConnectionError_ThrowsLlmProvider()
        {
            // The endpoint is unreachable — the detection GET itself fails.
            var handler = new FakeHandler { ThrowConnection = true };
            var gate = NewGate(handler);

            await Assert.ThrowsAsync<LlmProviderException>(() =>
                gate.EnsureModelLoadedAsync(SwitchableConfig(), CancellationToken.None));

            Assert.Equal(0, handler.PostCount);
        }

        // ---- Helpers --------------------------------------------------------

        private static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Status(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body) };

        private static HttpResponseMessage Models(string status, string model = "m") =>
            Ok($"{{\"data\":[{{\"id\":\"{model}\",\"status\":{{\"value\":\"{status}\"}}}}]}}");

        // ---- Fakes ----------------------------------------------------------

        // Each CreateClient hands back a fresh HttpClient over the shared handler (like the real pooled
        // factory), so the gate can set a per-client Timeout on the trigger client without hitting the
        // "instance has already started requests" guard.
        private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private int _getCount;
            private int _postCount;

            public Func<int, HttpResponseMessage>? OnGet;
            public Func<int, HttpResponseMessage>? OnPost;
            public bool ThrowConnection;

            public int GetCount => Volatile.Read(ref _getCount);
            public int PostCount => Volatile.Read(ref _postCount);
            public bool PostSeen => PostCount > 0;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (ThrowConnection)
                    throw new HttpRequestException("connection refused");

                if (request.Method == HttpMethod.Post)
                {
                    var i = Interlocked.Increment(ref _postCount) - 1;
                    return Task.FromResult(OnPost?.Invoke(i) ?? Ok("{}"));
                }

                var g = Interlocked.Increment(ref _getCount) - 1;
                return Task.FromResult(OnGet?.Invoke(g) ?? Models("loaded"));
            }
        }
    }
}
