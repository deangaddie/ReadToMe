using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.Transcription.Settings;
using Read2Me.Services.Health;

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
        ILogger<WhisperTranscriptionClient> logger,
        IAiServiceReporter reporter) : ITranscriptionClient
    {
        public async Task<string> TranscribeAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default)
        {
            var text = await PostAsrAsync(config, audio, fileName, "output=txt", ct);
            return text.Trim();
        }

        public async Task<IReadOnlyList<TranscribedWord>> TranscribeWithWordTimestampsAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default)
        {
            var json = await PostAsrAsync(
                config, audio, fileName, "output=json&word_timestamps=true", ct);

            using var doc = JsonDocument.Parse(json);
            var words = new List<TranscribedWord>();
            if (doc.RootElement.TryGetProperty("segments", out var segments))
            {
                foreach (var segment in segments.EnumerateArray())
                {
                    if (!segment.TryGetProperty("words", out var segmentWords))
                        continue;
                    foreach (var word in segmentWords.EnumerateArray())
                    {
                        words.Add(new TranscribedWord(
                            word.GetProperty("word").GetString() ?? string.Empty,
                            word.GetProperty("start").GetDouble(),
                            word.GetProperty("end").GetDouble()));
                    }
                }
            }

            return words;
        }

        private async Task<string> PostAsrAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            string outputQuery,
            CancellationToken ct)
        {
            var settings = JsonSerializer.Deserialize<LocalWhisperSettings>(config.SettingsJson)
                ?? new LocalWhisperSettings();

            logger.LogDebug("Sending {File} to Whisper at {Url} ({Query})", fileName, settings.BaseUrl, outputQuery);

            var http = httpClientFactory.CreateClient();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var content = new MultipartFormDataContent();
                var fileContent = new StreamContent(audio);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
                content.Add(fileContent, "audio_file", fileName);

                var url = settings.BaseUrl.TrimEnd('/') + "/asr?task=transcribe&" + outputQuery;
                var response = await http.PostAsync(url, content, ct);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(ct);
                sw.Stop();
                logger.LogDebug("Whisper responded for {File} in {Ms} ms ({Chars} chars)",
                    fileName, sw.ElapsedMilliseconds, body.Length);
                reporter.ReportSuccess(settings.BaseUrl);
                return body;
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
