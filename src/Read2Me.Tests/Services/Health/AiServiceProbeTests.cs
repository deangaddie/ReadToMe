using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Health
{
    public class AiServiceProbeTests : AppDbTestBase
    {
        private const string LlamaBaseUrl = "http://localhost:8080";
        private readonly FakeHttpClientFactory _httpFactory = new();
        private readonly FakeModelLoadGate _gate = new();

        // A DI scope containing the services a config-driven warm-up resolves: the fake HTTP factory
        // (used by health probes), the active-LLM-config store, and the model-load gate the llama
        // warm-up now routes through — the single model-load path in the app.
        private ServiceProvider BuildProvider(AiWatchdogOptions options)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpClientFactory>(_httpFactory);
            services.AddSingleton(Factory);
            services.AddSingleton<LlmSettingsService>();
            services.AddSingleton<IOptions<AiWatchdogOptions>>(Options.Create(options));
            services.AddSingleton<IModelLoadGate>(_gate);
            return services.BuildServiceProvider();
        }

        private AiServiceProbe CreateSut(out ServiceProvider provider, AiWatchdogOptions? options = null)
        {
            var opts = options ?? new AiWatchdogOptions
            {
                HealthPollTimeoutSeconds = 0,
                HealthPollIntervalSeconds = 0,
            };
            provider = BuildProvider(opts);
            return new AiServiceProbe(
                _httpFactory,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(opts),
                NullLogger<AiServiceProbe>.Instance);
        }

        // Seeds an active LLM config pointed at llama; the first config auto-activates.
        private static Task SeedActiveLlmConfig(ServiceProvider provider, string model) =>
            provider.GetRequiredService<LlmSettingsService>().CreateConfigAsync(new LlmServerConfig
            {
                Name = "llama",
                BaseUrl = LlamaBaseUrl,
                Model = model,
            });

        private static DockerAiService HealthOnly(string baseUrl = "http://health-test") =>
            new("test", "read2me-test", baseUrl, "/health");

        private static Task<HttpResponseMessage> Status(HttpStatusCode code) =>
            Task.FromResult(new HttpResponseMessage(code));

        [Fact]
        public async Task WaitUntilHealthy_ReturnsTrue_OnFirst2xx_SwallowingEarlierFailures()
        {
            // Connection refused, then 5xx, then 2xx — the probe must keep polling and succeed.
            _httpFactory.Responder = _ => _httpFactory.CallCount switch
            {
                1 => throw new HttpRequestException("connection refused"),
                2 => Status(HttpStatusCode.InternalServerError),
                _ => Status(HttpStatusCode.OK),
            };

            var options = new AiWatchdogOptions { HealthPollTimeoutSeconds = 60, HealthPollIntervalSeconds = 0 };
            var result = await CreateSut(out _, options).WaitUntilHealthyAsync(HealthOnly(), CancellationToken.None);

            Assert.True(result);
            Assert.Equal(3, _httpFactory.CallCount);
            Assert.EndsWith("/health", _httpFactory.LastRequest?.RequestUri?.ToString());
        }

        [Fact]
        public async Task WaitUntilHealthy_ReturnsFalse_OnTimeout()
        {
            _httpFactory.Responder = _ => Status(HttpStatusCode.ServiceUnavailable);

            // Timeout 0 → deadline is now; never sees a 2xx → false, no real delay.
            var result = await CreateSut(out _).WaitUntilHealthyAsync(HealthOnly(), CancellationToken.None);

            Assert.False(result);
        }

        [Fact]
        public async Task Warmup_Llama_RoutesActiveConfigThroughGate_ReturnsTrue()
        {
            // The single model-load path: the warm-up hands the active config to the gate.
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out var provider);
            await SeedActiveLlmConfig(provider, model: "gemma-26b_QAT");

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.True(result);
            Assert.Equal(1, _gate.CallCount);
            Assert.Equal("gemma-26b_QAT", _gate.LastConfig?.Model);
        }

        [Fact]
        public async Task Warmup_Llama_NoActiveConfig_ReturnsTrue_WithoutCallingGate()
        {
            // Nothing configured → nothing safe to warm; health alone is readiness.
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out _);

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.True(result);
            Assert.Equal(0, _gate.CallCount);
        }

        [Fact]
        public async Task Warmup_Llama_ModelStillLoading_ReturnsTrue_SoftOutcome_NoWatchdogTrip()
        {
            // A responsive-but-loading endpoint is soft/retryable: the load runs on out-of-band and the
            // real request waits on the gate, so warm-up reports readiness rather than a warm-up failure
            // (a false would drive the recovery loop into another restart).
            _gate.Behavior = (c, _) => throw new ModelStillLoadingException(
                c.BaseUrl, c.Model ?? string.Empty, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300));
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out var provider);
            await SeedActiveLlmConfig(provider, model: "gemma-26b_QAT");

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.True(result);
        }

        [Fact]
        public async Task Warmup_Llama_ProviderException_ReturnsFalse_WithoutEscaping()
        {
            // A genuinely unreachable endpoint stays a hard warm-up failure (false) — the normal path.
            _gate.Behavior = (_, _) => throw new LlmProviderException("llama unreachable", new HttpRequestException("boom"));
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out var provider);
            await SeedActiveLlmConfig(provider, model: "gemma-26b_QAT");

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.False(result);
        }

        [Fact]
        public async Task Warmup_ReturnsFalse_OnTimeout_WithoutEscaping()
        {
            _gate.Behavior = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
            };
            var registry = new DockerAiServiceRegistry();
            var options = new AiWatchdogOptions { WarmupTimeoutSeconds = 0 };
            var sut = CreateSut(out var provider, options);
            await SeedActiveLlmConfig(provider, model: "gemma-26b_QAT");

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.False(result);
        }

        [Fact]
        public async Task Warmup_NoDelegate_ReturnsTrue_WithoutHttpCall()
        {
            var result = await CreateSut(out _).WarmupAsync(HealthOnly(), CancellationToken.None);

            Assert.True(result);
            Assert.Equal(0, _httpFactory.CallCount);
        }

        // Stands in for the real ModelLoadGate: records the config it was handed and runs a
        // configurable behaviour so tests can drive the loaded / still-loading / unreachable outcomes.
        private sealed class FakeModelLoadGate : IModelLoadGate
        {
            public Func<LlmServerConfig, CancellationToken, Task> Behavior { get; set; } = (_, _) => Task.CompletedTask;
            public int CallCount { get; private set; }
            public LlmServerConfig? LastConfig { get; private set; }

            public Task EnsureModelLoadedAsync(LlmServerConfig config, CancellationToken ct)
            {
                CallCount++;
                LastConfig = config;
                return Behavior(config, ct);
            }
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            public Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder { get; set; } =
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            public int CallCount { get; private set; }
            public HttpRequestMessage? LastRequest { get; private set; }
            public string? LastRequestContent { get; private set; }
            public CancellationToken LastToken { get; private set; }

            public HttpClient CreateClient(string name) => new(new FakeHttpMessageHandler(this));

            private sealed class FakeHttpMessageHandler(FakeHttpClientFactory factory) : HttpMessageHandler
            {
                protected override async Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    factory.CallCount++;
                    factory.LastRequest = request;
                    factory.LastToken = cancellationToken;
                    if (request.Content != null)
                    {
                        factory.LastRequestContent = await request.Content.ReadAsStringAsync(cancellationToken);
                    }
                    return await factory.Responder(request);
                }
            }
        }
    }
}
