using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for Qwen3-Base voice cloning. Clones from reference audio plus its
    /// transcript (referenceTranscript -> voice_transcript / ref_text), with a language selector
    /// and optional HF sampling kwargs.
    /// </summary>
    public sealed class Qwen3ParagraphTtsClient(
        IHttpClientFactory httpClientFactory,
        ILogger<Qwen3ParagraphTtsClient> logger,
        IAiServiceReporter reporter) : IParagraphTtsClient
    {
        public async Task<Stream> GenerateAsync(
            string text,
            string? voiceInstructions,
            Stream referenceAudioStream,
            ParagraphTtsServiceConfig settings,
            string? settingsOverrideJson,
            string? referenceTranscript = null,
            CancellationToken ct = default)
        {
            // The Qwen3-Base server 422s on an empty voice_transcript; fail here with a clear
            // message instead of letting that read as a service outage.
            if (string.IsNullOrWhiteSpace(referenceTranscript))
                throw new InvalidOperationException(
                    "Qwen3-Base voice cloning requires a reference-audio transcript. " +
                    "Set the voice's transcript (or the voice-design sample text).");

            var cfg = VoiceDesignSettingsMerge.Merge<Qwen3ParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);
            var baseUrl = cfg.BaseUrl.TrimEnd('/');

            var http = httpClientFactory.CreateClient();
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

            try
            {
                var result = await SynthesizeAsync(http, baseUrl, text, referenceAudioStream, cfg, referenceTranscript, ct);
                logger.LogDebug("Qwen3-Base paragraph TTS: generated audio via {BaseUrl}", baseUrl);
                reporter.ReportSuccess(baseUrl);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (reporter.ReportFailure(baseUrl, ex))
                    throw new AiServiceUnavailableException(baseUrl, ex);
                throw;
            }
        }

        private static async Task<Stream> SynthesizeAsync(
            HttpClient http, string baseUrl, string text, Stream referenceAudioStream,
            Qwen3ParagraphTtsSettings cfg, string? referenceTranscript, CancellationToken ct)
        {
            using var content = new MultipartFormDataContent
            {
                { new StringContent(text), "text" },
                { new StringContent(referenceTranscript ?? ""), "voice_transcript" },
                { new StringContent(cfg.Language), "language" },
            };

            if (cfg.Temperature is { } temperature)
                content.Add(new StringContent(Inv(temperature)), "temperature");
            if (cfg.TopP is { } topP)
                content.Add(new StringContent(Inv(topP)), "top_p");
            if (cfg.TopK is { } topK)
                content.Add(new StringContent(topK.ToString(CultureInfo.InvariantCulture)), "top_k");
            if (cfg.RepetitionPenalty is { } repetitionPenalty)
                content.Add(new StringContent(Inv(repetitionPenalty)), "repetition_penalty");
            if (cfg.MaxNewTokens is { } maxNewTokens)
                content.Add(new StringContent(maxNewTokens.ToString(CultureInfo.InvariantCulture)), "max_new_tokens");

            var audioContent = new StreamContent(referenceAudioStream);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "reference_audio", "reference.wav");

            var response = await http.PostAsync(baseUrl + "/tts", content, ct);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStreamAsync(ct);
        }

        private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
