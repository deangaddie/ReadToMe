using Read2Me.Services;

namespace Read2Me.App.Services.Preflight
{
    /// <summary>
    /// Maps a task kind to the base URLs of the AI endpoints its active configs will call.
    /// A missing active config simply contributes nothing — pre-flight then has nothing to
    /// check and the task fails (or is guarded) downstream exactly as it does today.
    /// </summary>
    public interface IAiTaskRequirementsResolver
    {
        Task<IReadOnlyList<string>> GetRequiredBaseUrlsAsync(AiTaskKind task, CancellationToken ct);
    }

    public sealed class AiTaskRequirementsResolver(
        LlmSettingsService llmSettings,
        ParagraphTtsSettingsService ttsSettings,
        TranscriptionSettingsService transcriptionSettings,
        SemanticSimilaritySettingsService similaritySettings,
        VoiceDesignSettingsService voiceDesignSettings) : IAiTaskRequirementsResolver
    {
        public async Task<IReadOnlyList<string>> GetRequiredBaseUrlsAsync(AiTaskKind task, CancellationToken ct)
        {
            var urls = task switch
            {
                // Attribution runs the escalation chain, not the active config — the chain is what
                // actually gets called, so the chain is what must be up. (The active config is only
                // the chain's fallback when no chain is stored.)
                AiTaskKind.CharacterAttribution =>
                    await AttributionChainUrlsAsync(),
                AiTaskKind.VoicePromptGeneration or AiTaskKind.CharacterDiscovery or AiTaskKind.BookEdit =>
                    new[] { await LlmUrlAsync() },
                AiTaskKind.AudioGeneration =>
                    new[] { await TtsUrlAsync(), await TranscriptionUrlAsync(), await SimilarityUrlAsync() },
                AiTaskKind.VoiceDesignAudio =>
                    new[] { await VoiceDesignUrlAsync() },
                AiTaskKind.Transcription =>
                    new[] { await TranscriptionUrlAsync() },
                _ => throw new ArgumentOutOfRangeException(nameof(task)),
            };

            return urls.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<string?[]> AttributionChainUrlsAsync()
        {
            var chain = await llmSettings.GetAttributionChainAsync();
            return [.. chain.Select(s => ServiceConfigBaseUrls.For(s.Config))];
        }

        private async Task<string?> LlmUrlAsync()
        {
            var config = await llmSettings.GetActiveConfigAsync();
            return config is null ? null : ServiceConfigBaseUrls.For(config);
        }

        private async Task<string?> TtsUrlAsync()
        {
            var config = await ttsSettings.GetActiveConfigAsync();
            return config is null ? null : ServiceConfigBaseUrls.For(config);
        }

        private async Task<string?> TranscriptionUrlAsync()
        {
            var config = await transcriptionSettings.GetActiveConfigAsync();
            return config is null ? null : ServiceConfigBaseUrls.For(config);
        }

        private async Task<string?> SimilarityUrlAsync()
        {
            var config = await similaritySettings.GetActiveConfigAsync();
            return config is null ? null : ServiceConfigBaseUrls.For(config);
        }

        private async Task<string?> VoiceDesignUrlAsync()
        {
            var config = await voiceDesignSettings.GetActiveConfigAsync();
            return config is null ? null : ServiceConfigBaseUrls.For(config);
        }
    }
}
