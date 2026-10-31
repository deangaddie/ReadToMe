using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Health;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>One active config and the managed service its base URL resolves to (null: not managed).</summary>
    internal sealed record ActiveConfig<TConfig>(TConfig Config, DockerAiService? Service) where TConfig : class;

    /// <summary>
    /// The active AI configs a check judges, read once: the active LLM config, the attribution chain
    /// in order, paragraph TTS, voice design, transcription and similarity, each with its managed
    /// service. A Quick check's rows, Full plan, re-run flags and estimate all take one read, so
    /// they can't disagree about a config saved part-way through; a Full run reads its own at start.
    /// </summary>
    internal sealed record ActiveAiConfigs(
        ActiveConfig<LlmServerConfig>? Llm,
        IReadOnlyList<ActiveConfig<LlmServerConfig>> Chain,
        ActiveConfig<ParagraphTtsServiceConfig>? ParagraphTts,
        ActiveConfig<VoiceDesignServiceConfig>? VoiceDesign,
        ActiveConfig<TranscriptionServiceConfig>? Transcription,
        ActiveConfig<SemanticSimilarityServiceConfig>? Similarity)
    {
        /// <summary>The distinct managed services the configs use.</summary>
        public IReadOnlyList<DockerAiService> UsedServices =>
        [
            .. new[] { Llm?.Service, ParagraphTts?.Service, VoiceDesign?.Service, Transcription?.Service, Similarity?.Service }
                .Concat(Chain.Select(c => c.Service))
                .OfType<DockerAiService>()
                .DistinctBy(s => s.Name),
        ];

        /// <summary>
        /// The status of each used service, plus every GPU container when a used one is GPU (a GPU
        /// step's sweep stops them, and the estimate counts their restarts), keyed by service name.
        /// A Quick check takes one, so a container's row and the estimate can't disagree about it.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, AiServiceStatus>> ReadStatusesAsync(
            IAiServiceControl control, DockerAiServiceRegistry registry, CancellationToken ct)
        {
            var used = UsedServices;
            var rivals = used.Any(s => s.UsesGpu) ? registry.All.Where(s => s.UsesGpu) : [];
            var statuses = new Dictionary<string, AiServiceStatus>();
            foreach (var service in used.Concat(rivals).DistinctBy(s => s.Name))
                statuses[service.Name] = await control.GetStatusAsync(service, ct);
            return statuses;
        }

        /// <summary>Reads the settings services in one scope and resolves each config's base URL.</summary>
        public static async Task<ActiveAiConfigs> ReadAsync(SetupCheckServices deps)
        {
            await using var scope = deps.Scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var llmSettings = sp.GetRequiredService<LlmSettingsService>();

            var llm = await llmSettings.GetActiveConfigAsync();
            var chain = await llmSettings.GetAttributionChainAsync();
            var tts = await sp.GetRequiredService<ParagraphTtsSettingsService>().GetActiveConfigAsync();
            var design = await sp.GetRequiredService<VoiceDesignSettingsService>().GetActiveConfigAsync();
            var transcription = await sp.GetRequiredService<TranscriptionSettingsService>().GetActiveConfigAsync();
            var similarity = await sp.GetRequiredService<SemanticSimilaritySettingsService>().GetActiveConfigAsync();

            ActiveConfig<T>? Pair<T>(T? config, Func<T, string?> baseUrl) where T : class =>
                config is null ? null : new(config, baseUrl(config) is { } url ? deps.Control.Resolve(url) : null);

            return new ActiveAiConfigs(
                Pair(llm, ServiceConfigBaseUrls.For),
                [.. chain.Select(s => Pair(s.Config, ServiceConfigBaseUrls.For)!)],
                Pair(tts, ServiceConfigBaseUrls.For),
                Pair(design, ServiceConfigBaseUrls.For),
                Pair(transcription, ServiceConfigBaseUrls.For),
                Pair(similarity, ServiceConfigBaseUrls.For));
        }
    }
}
