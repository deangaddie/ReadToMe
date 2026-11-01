using System.Text.Json;
using Read2Me.AppData.Entities;
using Read2Me.Core.Exceptions;
using Read2Me.Services.AiServers;

namespace Read2Me.Services.Llm
{
    /// <summary>
    /// The LLM side's <c>GET /v1/models</c>, shared by <see cref="ModelLoadGate"/> and
    /// <see cref="OpenAiLlmClient"/>. The caller's client carries the config's <c>ApiKey</c>; a
    /// connection failure or a non-2xx answer throws <see cref="LlmProviderException"/>, and caller
    /// cancellation passes through.
    /// </summary>
    internal static class LlmModelList
    {
        public static async Task<ModelListing> ReadAsync(HttpClient http, LlmServerConfig config, CancellationToken ct)
        {
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

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new LlmProviderException(
                        $"LLM provider returned error ({response.StatusCode}): {error}", null!);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return ModelListing.Parse(doc.RootElement);
            }
        }
    }
}
