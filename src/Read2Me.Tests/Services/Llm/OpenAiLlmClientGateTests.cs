using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Llm
{
    /// <summary>
    /// The client's half of a model switch: the real request goes out only after the model-load gate
    /// has returned for the config it names, and not at all while the gate is still waiting. The
    /// gate's switch-and-poll is <see cref="ModelLoadGateTests"/>.
    /// </summary>
    public class OpenAiLlmClientGateTests
    {
        private static readonly LlmServerConfig Config = new()
        {
            Name = "Switchable", BaseUrl = "http://localhost:8080", Model = "qwen-28b", SupportsModelSwitch = true,
        };

        private readonly List<string> _log = [];

        private OpenAiLlmClient NewClient(IModelLoadGate gate) => new(
            new SingleClientFactory(new HttpClient(new RecordingHandler(_log))),
            NullLogger<OpenAiLlmClient>.Instance,
            Options.Create(new AiWatchdogOptions { StreamInactivitySeconds = 60 }),
            gate);

        [Fact]
        public async Task The_request_goes_out_only_after_the_gate_has_loaded_the_named_model()
        {
            var gate = new HeldGate(_log);
            var client = NewClient(gate);

            var content = new StringBuilder();
            var stream = Task.Run(async () =>
            {
                await foreach (var chunk in client.StreamChatAsync(Config, "prompt", ct: CancellationToken.None))
                    if (chunk.Content is { } c) content.Append(c);
            });

            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["gate:qwen-28b"], _log);   // the model is still loading: nothing sent yet

            gate.Release.SetResult();
            await stream.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(["gate:qwen-28b", "send:v1/chat/completions"], _log);
            Assert.Equal("hi", content.ToString());
        }

        // ---- Fakes ----------------------------------------------------------

        private sealed class HeldGate(List<string> log) : IModelLoadGate
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task EnsureModelLoadedAsync(LlmServerConfig config, CancellationToken ct)
            {
                lock (log) log.Add($"gate:{config.Model}");
                Entered.SetResult();
                await Release.Task.WaitAsync(ct);
            }
        }

        private sealed class RecordingHandler(List<string> log) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (log) log.Add($"send:{request.RequestUri!.AbsolutePath.TrimStart('/')}");
                const string body = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
                });
            }
        }

        private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => client;
        }
    }
}
