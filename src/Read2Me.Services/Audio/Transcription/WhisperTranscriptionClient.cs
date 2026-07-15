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
    /// targeting the pinned Whisper.CPP server. POSTs audio to
    /// <c>/inference</c> using the Whisper.CPP verbose-JSON multipart contract.
    /// Reads its base URL from the config's <see cref="LocalWhisperSettings"/> blob.
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
            using var doc = await PostInferenceAsync(config, audio, fileName, ct);
            return GetTranscript(doc.RootElement).Trim();
        }

        public async Task<IReadOnlyList<TranscribedWord>> TranscribeWithWordTimestampsAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default)
        {
            using var doc = await PostInferenceAsync(config, audio, fileName, ct);
            var transcript = GetTranscript(doc.RootElement);
            var words = new List<TranscribedWord>();
            if (doc.RootElement.TryGetProperty("segments", out var segments))
            {
                foreach (var segment in segments.EnumerateArray())
                {
                    if (!segment.TryGetProperty("words", out var segmentWords))
                        continue;
                    foreach (var word in segmentWords.EnumerateArray())
                    {
                        AddWord(words, word);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(transcript) && words.Count == 0)
                throw new InvalidDataException("Whisper returned a transcript without usable word timing.");

            return words;
        }

        private async Task<JsonDocument> PostInferenceAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct)
        {
            var settings = JsonSerializer.Deserialize<LocalWhisperSettings>(config.SettingsJson)
                ?? new LocalWhisperSettings();

            logger.LogDebug("Sending {File} to Whisper at {Url}", fileName, settings.BaseUrl);

            var http = httpClientFactory.CreateClient();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var content = new MultipartFormDataContent();
                var fileContent = new StreamContent(audio);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
                content.Add(fileContent, "file", fileName);
                content.Add(new StringContent("verbose_json"), "response_format");
                content.Add(new StringContent("en"), "language");
                content.Add(new StringContent("true"), "token_timestamps");
                content.Add(new StringContent("1"), "max_len");
                content.Add(new StringContent("true"), "split_on_word");

                var url = settings.BaseUrl.TrimEnd('/') + "/inference";
                var response = await http.PostAsync(url, content, ct);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(ct);
                sw.Stop();
                logger.LogDebug("Whisper responded for {File} in {Ms} ms ({Chars} chars)",
                    fileName, sw.ElapsedMilliseconds, body.Length);
                reporter.ReportSuccess(settings.BaseUrl);
                return JsonDocument.Parse(body);
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

        private static string GetTranscript(JsonElement root) =>
            root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : string.Empty;

        private static void AddWord(List<TranscribedWord> words, JsonElement wordRecord)
        {
            if (!wordRecord.TryGetProperty("word", out var wordProperty) ||
                wordProperty.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Whisper returned a malformed word timing record.");
            }

            var word = wordProperty.GetString()?.Trim() ?? string.Empty;
            if (word.Length == 0)
                return;

            if (!wordRecord.TryGetProperty("start", out var startProperty) ||
                !startProperty.TryGetDouble(out var start) ||
                !wordRecord.TryGetProperty("end", out var endProperty) ||
                !endProperty.TryGetDouble(out var end))
            {
                throw new InvalidDataException("Whisper returned a malformed word timing record.");
            }

            if (!double.IsFinite(start) || !double.IsFinite(end) || start > end)
                throw new InvalidDataException("Whisper returned invalid word timing.");

            if (IsStandalonePunctuation(word))
            {
                if (words.Count == 0)
                    throw new InvalidDataException("Whisper returned punctuation without a preceding word.");

                var previous = words[^1];
                if (end < previous.End)
                    throw new InvalidDataException("Whisper returned descending word timing.");

                words[^1] = previous with { Word = previous.Word + word, End = end };
                return;
            }

            if (words.Count > 0)
            {
                var previous = words[^1];
                if (start < previous.Start || end < previous.End)
                    throw new InvalidDataException("Whisper returned descending word timing.");
            }

            words.Add(new TranscribedWord(word, start, end));
        }

        private static bool IsStandalonePunctuation(string value) => value.All(char.IsPunctuation);

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
