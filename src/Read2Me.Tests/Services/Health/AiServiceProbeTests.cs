using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
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

        // A DI scope containing the services a config-driven warm-up resolves: the fake HTTP factory,
        // the active-LLM-config store, and the real LLM client (which posts through the same factory).
        private ServiceProvider BuildProvider(AiWatchdogOptions options)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpClientFactory>(_httpFactory);
            services.AddSingleton(Factory);
            services.AddSingleton<LlmSettingsService>();
            services.AddSingleton<IOptions<AiWatchdogOptions>>(Options.Create(options));
            services.AddSingleton<ILlmClient, OpenAiLlmClient>();
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
        public async Task Warmup_Llama_SendsOneTokenHiCompletion_WithConfiguredModel_ReturnsTrueOn2xx()
        {
            _httpFactory.Responder = _ => Status(HttpStatusCode.OK);
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out var provider);
            await SeedActiveLlmConfig(provider, model: "gemma-26b_QAT");

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.True(result);
            Assert.Equal(HttpMethod.Post, _httpFactory.LastRequest?.Method);
            Assert.EndsWith("/v1/chat/completions", _httpFactory.LastRequest?.RequestUri?.ToString());
            var body = _httpFactory.LastRequestContent ?? "";
            Assert.Contains("\"model\":\"gemma-26b_QAT\"", body);
            Assert.Contains("\"content\":\"hi\"", body);
            Assert.Contains("\"max_tokens\":1", body);
        }

        [Fact]
        public async Task Warmup_Llama_NoActiveConfig_ReturnsTrue_WithoutHttpCall()
        {
            // Nothing configured → nothing safe to warm; health alone is readiness.
            _httpFactory.Responder = _ => Status(HttpStatusCode.OK);
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out _);

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.True(result);
            Assert.Equal(0, _httpFactory.CallCount);
        }

        [Fact]
        public async Task Warmup_ReturnsFalse_WhenRequestThrows_WithoutEscaping()
        {
            _httpFactory.Responder = _ => throw new HttpRequestException("boom");
            var registry = new DockerAiServiceRegistry();
            var sut = CreateSut(out var provider);
            await SeedActiveLlmConfig(provider, model: "gemma-26b_QAT");

            var result = await sut.WarmupAsync(registry.GetByName("llama"), CancellationToken.None);

            Assert.False(result);
        }

        [Fact]
        public async Task Warmup_ReturnsFalse_OnTimeout_WithoutEscaping()
        {
            _httpFactory.Responder = async _ =>
            {
                await Task.Delay(Timeout.Infinite, _httpFactory.LastToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
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
