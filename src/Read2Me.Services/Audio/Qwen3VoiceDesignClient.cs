using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Voice-design client for Qwen3-TTS (port 8100). Sends a text description prompt
    /// and sample text to POST /tts and returns the generated audio stream.
    /// No reference audio is required — voice is synthesised from the description alone.
    /// </summary>
    public sealed class Qwen3VoiceDesignClient(
        IHttpClientFactory httpClientFactory,
        ILogger<Qwen3VoiceDesignClient> logger) : IVoiceDesignClient
    {
        public async Task<Stream> DesignVoiceAsync(
            AudioServerConfig config,
            string prompt,
            string sampleText,
            CancellationToken ct = default)
        {
            logger.LogDebug("Sending voice design request to {Url}", config.BaseUrl);

            var http = CreateClient(config);

            var payload = new
            {
                voice_description = prompt,
                text = sampleText,
                model = string.IsNullOrWhiteSpace(config.Model) ? null : config.Model,
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl.TrimEnd('/') + "/tts")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var ms = new MemoryStream();
            await response.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            return ms;
        }

        private HttpClient CreateClient(AudioServerConfig config)
        {
            var http = httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
            return http;
        }
    }
}
