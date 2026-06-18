using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.Transcription.Settings;

namespace Read2Me.Services.Audio.Transcription
{
    /// <summary>
    /// Transcription client for <see cref="TranscriptionServiceType.LocalWhisper"/>,
    /// targeting the ahmetoner/whisper-asr-webservice API. POSTs audio to
    /// /asr as multipart form data (field <c>audio_file</c>) with
    /// <c>output=txt</c>, returning the plain-text transcript body. Reads its
    /// base URL from the config's <see cref="LocalWhisperSettings"/> blob.
    /// </summary>
    public sealed class WhisperTranscriptionClient(
        IHttpClientFactory httpClientFactory,
        ILogger<WhisperTranscriptionClient> logger) : ITranscriptionClient
    {
        public async Task<string> TranscribeAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default)
        {
            var settings = JsonSerializer.Deserialize<LocalWhisperSettings>(config.SettingsJson)
                ?? new LocalWhisperSettings();

            logger.LogDebug("Sending {File} to Whisper at {Url}", fileName, settings.BaseUrl);

            var http = httpClientFactory.CreateClient();

            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(audio);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
            content.Add(fileContent, "audio_file", fileName);

            var url = settings.BaseUrl.TrimEnd('/') + "/asr?task=transcribe&output=txt";
            var response = await http.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Trim();
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
