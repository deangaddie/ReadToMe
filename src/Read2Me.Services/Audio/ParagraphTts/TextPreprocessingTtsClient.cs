using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Text;

namespace Read2Me.Services.Audio.ParagraphTts
{
    public sealed class TextPreprocessingTtsClient(
        IParagraphTtsClient inner,
        IServiceProvider services,
        ILogger<TextPreprocessingTtsClient> logger) : IParagraphTtsClient
    {
        public Task<Stream> GenerateAsync(
            string text,
            string? voiceInstructions,
            Stream referenceAudioStream,
            ParagraphTtsServiceConfig config,
            string? settingsOverrideJson,
            CancellationToken ct = default)
        {
            logger.LogDebug("TextPreprocessing: {StepCount} enabled steps [{Steps}], input {Chars} chars",
                config.EnabledStepIds.Count,
                string.Join(", ", config.EnabledStepIds),
                text.Length);

            var processed = text;
            using var scope = services.CreateScope();
            var subSource = scope.ServiceProvider.GetRequiredService<ITextSubstitutionStepSource>();
            foreach (var id in config.EnabledStepIds)
            {
                var step = services.GetKeyedService<ITextProcessingStep>(id) ?? subSource.Resolve(id);
                if (step is null)
                {
                    logger.LogWarning("Unknown text processing step ID '{StepId}' — skipping", id);
                    continue;
                }
                var before = processed;
                processed = step.Process(processed);
                if (processed != before)
                    logger.LogDebug("Step '{StepId}': changed {Before} -> {After}",
                        id, before, processed);
                else
                    logger.LogDebug("Step '{StepId}': no change", id);
            }

            if (processed != text)
                logger.LogDebug("TextPreprocessing complete: output {Chars} chars", processed.Length);

            return inner.GenerateAsync(processed, voiceInstructions, referenceAudioStream, config, settingsOverrideJson, ct);
        }
    }
}
