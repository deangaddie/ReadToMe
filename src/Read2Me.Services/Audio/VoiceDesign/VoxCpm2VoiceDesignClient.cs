using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>
    /// Voice-design client for VoxCPM2 (POST /api/stream). Reads the length-prefixed
    /// binary frame protocol and returns a 16-bit PCM WAV stream.
    /// </summary>
    public sealed class VoxCpm2VoiceDesignClient(
        IHttpClientFactory httpClientFactory,
        ILogger<VoxCpm2VoiceDesignClient> logger) : IVoiceDesignClient
    {
        public async Task<Stream> DesignVoiceAsync(
            VoiceDesignServiceConfig config,
            string prompt,
            string sampleText,
            string? settingsOverrideJson,
            CancellationToken ct = default)
        {
            var settings = VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(
                config.SettingsJson, settingsOverrideJson);

            var http = httpClientFactory.CreateClient();
            var payload = new
            {
                text = sampleText,
                control = prompt,
                cfg_value = settings.CfgValue,
                inference_timesteps = settings.InferenceTimesteps,
                min_len = settings.MinLen,
                max_len = settings.MaxLen,
                normalize = settings.Normalize,
                denoise = settings.Denoise,
                retry_badcase = settings.RetryBadcase,
                retry_badcase_max_times = settings.RetryBadcaseMaxTimes,
                retry_badcase_ratio_threshold = settings.RetryBadcaseRatioThreshold,
            };
            var json = JsonSerializer.Serialize(payload);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, settings.BaseUrl.TrimEnd('/') + "/api/stream")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            int sampleRate = 48000;
            var pcm = new MemoryStream();
            var header = new byte[5]; // 1 type + 4 length

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

            logger.LogDebug("VoxCPM2 returned {Bytes} PCM bytes @ {Rate}Hz", pcm.Length, sampleRate);
            return WavWriter.WriteInt16Pcm(pcm.GetBuffer().AsSpan(0, (int)pcm.Length), sampleRate);
        }

        // Reads up to buffer.Length bytes; returns 0 at clean EOF, or the count read.
        // Uses ReadExactly semantics for the 5-byte header but tolerates EOF.
        private static async Task<int> ReadAtMostAsync(
            Stream s, byte[] buffer, CancellationToken ct)
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
