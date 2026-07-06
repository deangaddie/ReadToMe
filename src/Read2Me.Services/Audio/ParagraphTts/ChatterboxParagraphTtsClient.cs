using System.Globalization;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for Chatterbox. Single multipart POST to /tts, returns the WAV
    /// response as-is. No free-text instruction channel: voiceInstructions and
    /// referenceTranscript are ignored, expression comes from exaggeration/temperature knobs.
    /// </summary>
    public sealed class ChatterboxParagraphTtsClient(
        IHttpClientFactory httpClientFactory,
        ILogger<ChatterboxParagraphTtsClient> logger,
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
            var cfg = VoiceDesignSettingsMerge.Merge<ChatterboxParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);
            var baseUrl = cfg.BaseUrl.TrimEnd('/');

            var http = httpClientFactory.CreateClient();

            try
            {
                var result = await SynthesizeAsync(http, baseUrl, text, referenceAudioStream, cfg, ct);
                logger.LogDebug("Chatterbox paragraph TTS: generated audio via {BaseUrl}", baseUrl);
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
            ChatterboxParagraphTtsSettings cfg, CancellationToken ct)
        {
            using var content = new MultipartFormDataContent
            {
                { new StringContent(text), "text" },
                { new StringContent(Inv(cfg.Exaggeration)), "exaggeration" },
                { new StringContent(Inv(cfg.CfgWeight)), "cfg_weight" },
                { new StringContent(Inv(cfg.Temperature)), "temperature" },
                { new StringContent(Inv(cfg.MinP)), "min_p" },
                { new StringContent(Inv(cfg.TopP)), "top_p" },
                { new StringContent(Inv(cfg.RepetitionPenalty)), "repetition_penalty" },
            };
            var audioContent = new StreamContent(referenceAudioStream);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "reference_audio", "reference.wav");

            var response = await http.PostAsync(baseUrl + "/tts", content, ct);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStreamAsync(ct);
        }

        private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
