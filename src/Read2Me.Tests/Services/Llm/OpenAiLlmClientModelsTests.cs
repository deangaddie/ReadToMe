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
    /// <summary>
    /// <see cref="OpenAiLlmClient.GetModelsAsync"/>: the ids <c>GET /v1/models</c> lists, in server
    /// order, and an <see cref="LlmProviderException"/> when the server doesn't answer 2xx. The
    /// listing's parsing itself is <c>ModelListingTests</c>.
    /// </summary>
    public class OpenAiLlmClientModelsTests
    {
        private static readonly LlmServerConfig Config =
            new() { Name = "Test", BaseUrl = "http://localhost:8080", Model = "m" };

        private static OpenAiLlmClient NewClient(HttpStatusCode status, string body) => new(
            new SingleClientFactory(new HttpClient(new ModelsHandler(status, body))),
            NullLogger<OpenAiLlmClient>.Instance,
            Options.Create(new AiWatchdogOptions { StreamInactivitySeconds = 60 }),
            new NoOpModelLoadGate());

        [Fact]
        public async Task Lists_the_ids_in_server_order()
        {
            var client = NewClient(HttpStatusCode.OK,
                """{"data":[{"id":"qwen-28b","status":{"value":"loaded"}},{"id":"gemma-12b","status":{"value":"unloaded"}},{"id":"a-model"}]}""");

            var models = await client.GetModelsAsync(Config, CancellationToken.None);

            Assert.Equal(["qwen-28b", "gemma-12b", "a-model"], models);
        }

        [Fact]
        public async Task A_non_2xx_answer_throws_a_provider_exception()
        {
            var client = NewClient(HttpStatusCode.ServiceUnavailable, "loading");

            var ex = await Assert.ThrowsAsync<LlmProviderException>(
                () => client.GetModelsAsync(Config, CancellationToken.None));

            Assert.Contains("ServiceUnavailable", ex.Message);
            Assert.Contains("loading", ex.Message);
        }

        // ---- Fakes ----------------------------------------------------------

        private sealed class ModelsHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("http://localhost:8080/v1/models", request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => client;
        }

        private sealed class NoOpModelLoadGate : IModelLoadGate
        {
            public Task EnsureModelLoadedAsync(LlmServerConfig config, CancellationToken ct) => Task.CompletedTask;
        }
    }
}
