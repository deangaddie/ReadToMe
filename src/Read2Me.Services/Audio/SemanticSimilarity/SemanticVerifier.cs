using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;

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
                var score = await client.ComputeAsync(config, source, transcript, ct);

                return (score >= settings.PassThreshold, score, settings.PassThreshold);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Semantic similarity check failed; WER fail stands.");
                return (false, null, null);
            }
        }
    }
}
