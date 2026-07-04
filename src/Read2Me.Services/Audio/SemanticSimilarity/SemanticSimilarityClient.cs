using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.SemanticSimilarity
{
    public sealed class SemanticSimilarityClient(
        IHttpClientFactory httpClientFactory,
        ILogger<SemanticSimilarityClient> logger,
        IAiServiceReporter reporter) : ISemanticSimilarityClient
    {
        public async Task<double> ComputeAsync(
            SemanticSimilarityServiceConfig config,
            string text1,
            string text2,
            CancellationToken ct = default)
        {
            var settings = JsonSerializer.Deserialize<SemanticSimilaritySettings>(config.SettingsJson)
                ?? new SemanticSimilaritySettings();

            logger.LogDebug("Sending similarity request to {Url}", settings.BaseUrl);

            var http = httpClientFactory.CreateClient();
            var url = settings.BaseUrl.TrimEnd('/') + "/similarity";

            try
            {
                var response = await http.PostAsJsonAsync(url, new { text1, text2 }, ct);
                response.EnsureSuccessStatusCode();

                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                var similarity = doc.RootElement.GetProperty("similarity").GetDouble();
                reporter.ReportSuccess(settings.BaseUrl);
                return similarity;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (reporter.ReportFailure(settings.BaseUrl, ex))
                    throw new AiServiceUnavailableException(settings.BaseUrl, ex);
                throw;
            }
        }
    }
}
