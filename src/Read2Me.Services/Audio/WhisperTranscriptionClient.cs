using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Whisper-compatible transcription client. POSTs audio to /v1/audio/transcriptions
    /// using multipart form data (OpenAI Whisper API format).
    /// </summary>
    public sealed class WhisperTranscriptionClient(
        IHttpClientFactory httpClientFactory,
        ILogger<WhisperTranscriptionClient> logger) : ITranscriptionClient
    {
        public async Task<string> TranscribeAsync(
            AudioServerConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default)
        {
            logger.LogDebug("Sending {File} to Whisper at {Url}", fileName, config.BaseUrl);

            var http = CreateClient(config);

            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(audio);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "file", fileName);
            content.Add(new StringContent("whisper-1"), "model");

            var url = config.BaseUrl.TrimEnd('/') + "/v1/audio/transcriptions";
            using var response = await http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
        }

        private HttpClient CreateClient(AudioServerConfig config)
        {
            var http = httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
            return http;
        }

        private static string GetMimeType(string fileName) =>
            System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".wav" => "audio/wav",
                ".aac" => "audio/aac",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream",
            };
    }
}
