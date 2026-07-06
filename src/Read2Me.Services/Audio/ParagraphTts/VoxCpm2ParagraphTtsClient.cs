using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for VoxCPM2. Uploads reference audio to /upload-audio,
    /// then streams binary frames from /api/stream and returns a 16-bit PCM WAV stream.
    /// </summary>
    public sealed class VoxCpm2ParagraphTtsClient(
        IHttpClientFactory httpClientFactory,
        ILogger<VoxCpm2ParagraphTtsClient> logger,
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
            var cfg = VoiceDesignSettingsMerge.Merge<VoxCpm2ParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);
            var baseUrl = cfg.BaseUrl.TrimEnd('/');

            var http = httpClientFactory.CreateClient();

            try
            {
                // Step 1: upload reference audio
                var fileId = await UploadReferenceAudioAsync(http, baseUrl, referenceAudioStream, ct);

                // Step 2: stream synthesis
                var result = await StreamSynthesisAsync(http, baseUrl, text, voiceInstructions, fileId, cfg, ct);
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

        private static async Task<string> UploadReferenceAudioAsync(
            HttpClient http, string baseUrl, Stream audio, CancellationToken ct)
        {
            using var content = new MultipartFormDataContent();
            var audioContent = new StreamContent(audio);
            audioContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "file", "reference.wav");

            var response = await http.PostAsync(baseUrl + "/upload-audio", content, ct);
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return doc.RootElement.GetProperty("file_id").GetString()
                ?? throw new InvalidOperationException("upload-audio returned no file_id.");
        }

        private async Task<Stream> StreamSynthesisAsync(
            HttpClient http, string baseUrl,
            string text, string? control, string fileId, VoxCpm2ParagraphTtsSettings cfg,
            CancellationToken ct)
        {
            var payload = new
            {
                text,
                control = control ?? string.Empty,
                reference_wav_path = fileId,
                cfg_value = cfg.CfgValue,
                inference_timesteps = cfg.InferenceTimesteps,
                min_len = cfg.MinLen,
                max_len = cfg.MaxLen,
                normalize = cfg.Normalize,
                denoise = cfg.Denoise,
                retry_badcase = cfg.RetryBadcase,
                retry_badcase_max_times = cfg.RetryBadcaseMaxTimes,
                retry_badcase_ratio_threshold = cfg.RetryBadcaseRatioThreshold,
            };
            var json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/stream")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            int sampleRate = 48000;
            var pcm = new MemoryStream();
            var header = new byte[5];

            while (true)
            {
                int read = await ReadAtMostAsync(stream, header, ct);
                if (read == 0) break;
                if (read < 5) throw new InvalidOperationException("Truncated frame header.");

                byte type = header[0];
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1, 4));
                var payloadBuf = new byte[len];
                await stream.ReadExactlyAsync(payloadBuf, ct);

                if (type == 0) // JSON control
                {
                    using var doc = JsonDocument.Parse(payloadBuf);
                    var msgType = doc.RootElement.GetProperty("type").GetString();
                    if (msgType == "meta"
                        && doc.RootElement.TryGetProperty("sample_rate", out var sr))
                        sampleRate = sr.GetInt32();
                    else if (msgType == "done")
                        break;
                    else if (msgType == "error")
                        throw new InvalidOperationException(
                            doc.RootElement.GetProperty("message").GetString());
                }
                else if (type == 1) // float32 PCM
                {
                    pcm.Write(payloadBuf, 0, payloadBuf.Length);
                }
            }

            logger.LogDebug("VoxCPM2 paragraph TTS: {Bytes} PCM bytes @ {Rate}Hz", pcm.Length, sampleRate);
            return WavWriter.WriteInt16Pcm(pcm.GetBuffer().AsSpan(0, (int)pcm.Length), sampleRate);
        }

        private static async Task<int> ReadAtMostAsync(Stream s, byte[] buffer, CancellationToken ct)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await s.ReadAsync(buffer.AsMemory(total), ct);
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }
}
