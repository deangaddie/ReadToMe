using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Read2Me.Services.Health;

namespace Read2Me.Services.Audio.SemanticSimilarity
{
    public sealed class SemanticVerifier(
        SemanticSimilaritySettingsService settingsService,
        ISemanticSimilarityClientResolver clientResolver,
        ILogger<SemanticVerifier> logger) : ISemanticVerifier
    {
        public async Task<(bool Passes, double? Score, double? Threshold)> PassesAsync(
            string source, string transcript, CancellationToken ct = default)
        {
            try
            {
                var config = await settingsService.GetActiveConfigAsync();
                if (config is null)
                {
                    logger.LogDebug("No active semantic similarity config; skipping rescue.");
                    return (false, null, null);
                }

                var settings = string.IsNullOrWhiteSpace(config.SettingsJson)
                    ? new SemanticSimilaritySettings()
                    : JsonSerializer.Deserialize<SemanticSimilaritySettings>(config.SettingsJson)
                      ?? new SemanticSimilaritySettings();

                var client = clientResolver.Resolve(config.Type);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var score = await client.ComputeAsync(config, source, transcript, ct);
                sw.Stop();

                logger.LogDebug(
                    "Semantic similarity ({Provider}): score {Score:0.000} vs threshold {Threshold:0.000} in {Ms} ms",
                    config.Type, score, settings.PassThreshold, sw.ElapsedMilliseconds);

                return (score >= settings.PassThreshold, score, settings.PassThreshold);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AiServiceUnavailableException ex)
            {
                // The one AI outage in the pipeline that must *not* reach RunAsync's edge. The
                // semantic check is a rescue for an already-failed WER, so a down similarity
                // service degrades to "not rescued" — it cannot fail an item that TTS and
                // transcription both served.
                logger.LogWarning(ex, "Semantic similarity service unavailable; not rescued, WER fail stands.");
                return (false, null, null);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Semantic similarity check failed; WER fail stands.");
                return (false, null, null);
            }
        }
    }
}
