using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.AudioCpp
{
    /// <summary>
    /// The one way the app talks to the audio.cpp TTS runtime. Every audio.cpp-backed provider maps
    /// its typed settings onto an <see cref="AudioCppSpeechRequest"/> and calls this.
    /// </summary>
    public interface IAudioCppClient
    {
        /// <summary>
        /// Posts the request through the endpoint's <see cref="IAudioCppGate"/> and returns the WAV
        /// audio.cpp answers, at the model's native rate. Throws <see cref="TtsBusyException"/> when
        /// the endpoint stays busy with another model through every retry, and
        /// <see cref="AiServiceUnavailableException"/> when a managed endpoint fails outright.
        /// </summary>
        Task<Stream> SpeakAsync(string baseUrl, AudioCppSpeechRequest request, CancellationToken ct);
    }

    /// <summary>Backoff between 503 retries; the retry count is the number of delays.</summary>
    public sealed record AudioCppRetryPolicy(IReadOnlyList<TimeSpan> Delays)
    {
        public static AudioCppRetryPolicy Default { get; } = new(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)]);
    }

    public sealed class AudioCppClient(
        IHttpClientFactory httpClientFactory,
        IAudioCppGate gate,
        IAiServiceReporter reporter,
        AudioCppRetryPolicy retryPolicy,
        ILogger<AudioCppClient> logger) : IAudioCppClient
    {
        /// <summary>
        /// Named client with a 300 s timeout: a cold first request after a model switch takes up to
        /// ~30 s on top of generation, and the default 100 s would cut long paragraphs off.
        /// </summary>
        public const string HttpClientName = "audiocpp";

        public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(300);

        public async Task<Stream> SpeakAsync(string baseUrl, AudioCppSpeechRequest request, CancellationToken ct)
        {
            baseUrl = baseUrl.TrimEnd('/');
            try
            {
                var wav = await gate.RunAsync(baseUrl, request.ModelId,
                    token => PostWithRetryAsync(baseUrl, request, token), ct);
                reporter.ReportSuccess(baseUrl);
                return wav;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TtsBusyException)
            {
                // Alive and busy, not down: the watchdog must not restart a container mid-generation.
                throw;
            }
            catch (Exception ex)
            {
                if (reporter.ReportFailure(baseUrl, ex))
                    throw new AiServiceUnavailableException(baseUrl, ex);
                throw;
            }
        }

        private async Task<Stream> PostWithRetryAsync(string baseUrl, AudioCppSpeechRequest request, CancellationToken ct)
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            var json = BuildBody(request);

            for (var attempt = 0; ; attempt++)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(baseUrl + "/v1/audio/speech", content, ct);

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    if (attempt >= retryPolicy.Delays.Count)
                        throw new TtsBusyException(baseUrl, request.ModelId);

                    var delay = retryPolicy.Delays[attempt];
                    logger.LogInformation("audio.cpp {BaseUrl} busy (503) for {Model}; retrying in {Delay:0.#}s",
                        baseUrl, request.ModelId, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"audio.cpp returned {(int)response.StatusCode} ({response.StatusCode}) for {request.ModelId}: {error}",
                        null, response.StatusCode);
                }

                var wav = new MemoryStream();
                await response.Content.CopyToAsync(wav, ct);
                wav.Position = 0;
                return wav;
            }
        }

        internal static string BuildBody(AudioCppSpeechRequest request)
        {
            var body = new JsonObject
            {
                ["model"] = request.ModelId,
                ["input"] = request.Input,
            };
            if (request.VoiceRef is { } voiceRef)
                body["voice_ref"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["data"] = Convert.ToBase64String(voiceRef),
                };
            if (!string.IsNullOrWhiteSpace(request.ReferenceText))
                body["reference_text"] = request.ReferenceText;
            if (!string.IsNullOrWhiteSpace(request.Language))
                body["language"] = request.Language;

            var options = new JsonObject();
            foreach (var (key, value) in request.Options)
                options[key] = value;
            body["options"] = options;

            return body.ToJsonString();
        }
    }
}
