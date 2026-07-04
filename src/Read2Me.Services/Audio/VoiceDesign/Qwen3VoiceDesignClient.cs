using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>Voice-design client for Qwen3-TTS (POST /tts). Returns WAV directly.</summary>
    public sealed class Qwen3VoiceDesignClient(
        IHttpClientFactory httpClientFactory,
        ILogger<Qwen3VoiceDesignClient> logger,
        IAiServiceReporter reporter) : IVoiceDesignClient
    {
        public async Task<Stream> DesignVoiceAsync(
            VoiceDesignServiceConfig config,
            string prompt,
            string sampleText,
            string? settingsOverrideJson,
            CancellationToken ct = default)
        {
            var settings = VoiceDesignSettingsMerge.Merge<Qwen3VoiceDesignSettings>(
                config.SettingsJson, settingsOverrideJson);

            logger.LogDebug("Qwen3 voice design -> {Url}", settings.BaseUrl);

            var http = httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            try
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(sampleText), "text" },
                    { new StringContent(prompt), "voice_description" },
                };

                var request = new HttpRequestMessage(
                    HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/tts")
                {
                    Content = form,
                };

                var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var ms = new MemoryStream();
                await response.Content.CopyToAsync(ms, ct);
                ms.Position = 0;
                reporter.ReportSuccess(settings.BaseUrl);
                return ms;
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
