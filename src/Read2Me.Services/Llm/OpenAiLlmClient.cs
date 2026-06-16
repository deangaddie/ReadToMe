using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// LLM client for OpenAI-compatible servers (e.g. llama.cpp). Streams chat
    /// completions over SSE and lists models via the /v1/models endpoint.
    /// </summary>
    public sealed class OpenAiLlmClient : ILlmClient
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public OpenAiLlmClient(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
            LlmServerConfig config, string prompt,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var http = CreateClient(config);

            var body = OpenAiRequestBuilder.BuildChatBody(config, prompt, stream: true);
            using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiStreamParser.Combine(config.BaseUrl, "v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var result = OpenAiStreamParser.ParseLine(line);
                if (result.Kind == OpenAiStreamParser.LineKind.Done)
                    break;
                if (result.Kind == OpenAiStreamParser.LineKind.Chunk)
                    yield return result.Chunk!;
            }

            yield return new LlmChatChunk(null, null, Done: true);
        }

        public async Task<IReadOnlyList<string>> GetModelsAsync(
            LlmServerConfig config, CancellationToken ct = default)
        {
            var http = CreateClient(config);

            using var response = await http.GetAsync(OpenAiStreamParser.Combine(config.BaseUrl, "v1/models"), ct);
            response.EnsureSuccessStatusCode();

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
