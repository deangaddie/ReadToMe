using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services.Health;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// LLM client for OpenAI-compatible servers (e.g. llama.cpp). Streams chat
    /// completions over SSE and lists models via the /v1/models endpoint.
    /// </summary>
    public sealed class OpenAiLlmClient : ILlmClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenAiLlmClient> _logger;
        private readonly AiWatchdogOptions _watchdogOptions;

        public OpenAiLlmClient(
            IHttpClientFactory httpClientFactory,
            ILogger<OpenAiLlmClient> logger,
            IOptions<AiWatchdogOptions> watchdogOptions)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _watchdogOptions = watchdogOptions.Value;
        }

        public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
            LlmServerConfig config, string prompt, string? jsonSchema = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _logger.LogTrace("LLM prompt:\n{Prompt}", prompt);

            var http = CreateClient(config);

            var body = OpenAiRequestBuilder.BuildChatBody(config, prompt, stream: true, jsonSchema);
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiStreamParser.Combine(config.BaseUrl, "v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled (queue cancel / circuit teardown). Wrapping it as a provider
                // failure would make it look like the server died: the reporter would count it toward
                // watchdog recovery and the attribution chain would escalate to the next config.
                throw;
            }
            catch (Exception ex)
            {
                throw new LlmProviderException($"Failed to connect to LLM provider at {config.BaseUrl}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new LlmProviderException($"LLM provider returned error ({response.StatusCode}): {error}", null!);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var thinkingBuilder = new StringBuilder();
            var responseBuilder = new StringBuilder();

            // Inactivity watchdog: race each read against a sliding timeout reset on every line, so a
            // wedged stream (no chunk for the window) is aborted instead of waited on forever.
            var inactivity = TimeSpan.FromSeconds(Math.Max(1, _watchdogOptions.StreamInactivitySeconds));

            while (true)
            {
                string? line;
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeoutCts.CancelAfter(inactivity);
                    try
                    {
                        line = await reader.ReadLineAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw new AiServiceStalledException(config.BaseUrl, inactivity);
                    }
                }

                if (line is null)
                    break;

                var result = OpenAiStreamParser.ParseLine(line);
                if (result.Kind == OpenAiStreamParser.LineKind.Done)
                    break;
                if (result.Kind == OpenAiStreamParser.LineKind.Chunk)
                {
                    var chunk = result.Chunk!;
                    if (chunk.Thinking is { } thinking)
                        thinkingBuilder.Append(thinking);
                    if (chunk.Content is { } content)
                        responseBuilder.Append(content);
                    yield return chunk;
                }
            }

            if (thinkingBuilder.Length > 0)
                _logger.LogTrace("LLM thinking:\n{Thinking}", thinkingBuilder.ToString());

            _logger.LogTrace("LLM response:\n{Response}", responseBuilder.ToString());

            yield return new LlmChatChunk(null, null, Done: true);
        }

        public async Task<IReadOnlyList<string>> GetModelsAsync(
            LlmServerConfig config, CancellationToken ct = default)
        {
            var http = CreateClient(config);

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(OpenAiStreamParser.Combine(config.BaseUrl, "v1/models"), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new LlmProviderException($"Failed to connect to LLM provider at {config.BaseUrl}", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new LlmProviderException($"LLM provider returned error ({response.StatusCode}): {error}", null!);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var value = id.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            models.Add(value);
                    }
                }
            }

            return models;
        }

        private HttpClient CreateClient(LlmServerConfig config)
        {
            var http = _httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
            return http;
        }

    }
}
