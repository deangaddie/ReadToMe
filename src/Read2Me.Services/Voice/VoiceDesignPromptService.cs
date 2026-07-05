using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Voice
{
    /// <summary>
    /// Generates a voice-design text prompt by calling the LLM with the configured
    /// voice prompt template, substituting book/author/character details.
    /// </summary>
    public class VoiceDesignPromptService
    {
        private readonly ILlmClient llm;
        private readonly LlmSettingsService settings;
        private readonly LlmPromptService prompts;
        private readonly ILogger<VoiceDesignPromptService> logger;
        private readonly EventBroadcaster<LlmStreamEvent>? broadcaster;

        public VoiceDesignPromptService(
            ILlmClient llm,
            LlmSettingsService settings,
            LlmPromptService prompts,
            ILogger<VoiceDesignPromptService> logger,
            EventBroadcaster<LlmStreamEvent>? broadcaster = null)
        {
            this.llm = llm;
            this.settings = settings;
            this.prompts = prompts;
            this.logger = logger;
            this.broadcaster = broadcaster;
            if (prompts != null)
                prompts.OnChanged += () =>
                {
                    _cachedVoicePromptTemplate = null;
                    _cachedVoicePlanTemplate = null;
                    _cachedNarratorVoicePlanTemplate = null;
                };
        }

        public enum GenerateStatus { Success, NoLlmConfigured, Failed }

        public sealed record GenerateResult(GenerateStatus Status, string? Prompt, string? FailureReason);

        public sealed record PlanResult(GenerateStatus Status, IReadOnlyList<VoicePlanVoice>? Voices, string? FailureReason);

        private string? _cachedVoicePromptTemplate;
        private string? _cachedVoicePlanTemplate;
        private string? _cachedNarratorVoicePlanTemplate;

        public virtual async Task<string> BuildRenderedPromptAsync(
            string bookTitle,
            string author,
            string characterName)
        {
            _cachedVoicePromptTemplate ??= await prompts.GetVoicePromptAsync();
            return PromptTemplates.Render(_cachedVoicePromptTemplate, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]     = bookTitle,
                [PromptTemplates.BookAuthor]    = author,
                [PromptTemplates.CharacterName] = characterName,
            });
        }

        public virtual async Task<string> BuildRenderedPlanPromptAsync(
            string bookTitle,
            string author,
            string characterName,
            bool isNarrator = false)
        {
            string template;
            if (isNarrator)
                template = _cachedNarratorVoicePlanTemplate ??= await prompts.GetNarratorVoicePlanPromptAsync();
            else
                template = _cachedVoicePlanTemplate ??= await prompts.GetVoicePlanPromptAsync();
            return PromptTemplates.Render(template, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]      = bookTitle,
                [PromptTemplates.BookAuthor]     = author,
                [PromptTemplates.CharacterName]  = characterName,
                [PromptTemplates.ResponseFormat] = VoicePlanSchema.JsonExample,
            });
        }

        /// <summary>
        /// Asks the LLM for the full set of voices a character needs across the book.
        /// Returns one entry per voice with name, description and design prompt.
        /// </summary>
        public virtual async Task<PlanResult> GeneratePlanAsync(
            string bookTitle,
            string author,
            string characterName,
            bool isNarrator = false,
            CancellationToken ct = default)
        {
            var config = await settings.GetActiveConfigAsync();
            if (config == null)
            {
                logger.LogWarning("No active LLM config — cannot generate voice plan");
                return new PlanResult(GenerateStatus.NoLlmConfigured, null, "No active LLM server configured");
            }

            try
            {
                var rendered = await BuildRenderedPlanPromptAsync(bookTitle, author, characterName, isNarrator);

                broadcaster?.Publish(new RequestStarted($"Voice plan: {characterName}", rendered));
                var metrics = new StreamMetrics(rendered);
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder();
                await foreach (var chunk in llm.StreamChatAsync(config, rendered, VoicePlanSchema.JsonSchema, ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster?.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } delta)
                    {
                        sb.Append(delta);
                        metrics.AddOutput(delta);
                        broadcaster?.Publish(new ContentDelta(delta));
                    }
                }
                sw.Stop();
                broadcaster?.Publish(new StreamCompleted(metrics.TokensIn, metrics.TokensOut,
                    sw.Elapsed.TotalSeconds, metrics.TokensPerSecond(sw.Elapsed.TotalSeconds)));

                if (!VoicePlanParser.TryParse(sb.ToString(), out var voices))
                {
                    const string reason = "LLM response was not a valid voice-plan JSON array";
                    broadcaster?.Publish(new StreamFailed(reason));
                    return new PlanResult(GenerateStatus.Failed, null, reason);
                }

                return new PlanResult(GenerateStatus.Success, voices, null);
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Failed to generate voice plan for {Character}", characterName);
                broadcaster?.Publish(new StreamFailed(ex.Message));
                return new PlanResult(GenerateStatus.Failed, null, ex.Message);
            }
        }

        public async Task<GenerateResult> GenerateAsync(
            string bookTitle,
            string author,
            string characterName,
            CancellationToken ct = default) =>
            await GenerateWithPromptAsync(
                await BuildRenderedPromptAsync(bookTitle, author, characterName),
                ct);

        public virtual async Task<GenerateResult> GenerateWithPromptAsync(
            string renderedPrompt,
            CancellationToken ct = default)
        {
            var config = await settings.GetActiveConfigAsync();
            if (config == null)
            {
                logger.LogWarning("No active LLM config — cannot generate voice design prompt");
                return new GenerateResult(GenerateStatus.NoLlmConfigured, null, "No active LLM server configured");
            }

            try
            {
                var rendered = renderedPrompt;

                broadcaster?.Publish(new RequestStarted("Voice prompt", rendered));
                var metrics = new StreamMetrics(rendered);
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder();
                await foreach (var chunk in llm.StreamChatAsync(config, rendered, ct: ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster?.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } delta)
                    {
                        sb.Append(delta);
                        metrics.AddOutput(delta);
                        broadcaster?.Publish(new ContentDelta(delta));
                    }
                }
                sw.Stop();
                broadcaster?.Publish(new StreamCompleted(metrics.TokensIn, metrics.TokensOut,
                    sw.Elapsed.TotalSeconds, metrics.TokensPerSecond(sw.Elapsed.TotalSeconds)));

                return new GenerateResult(GenerateStatus.Success, sb.ToString().Trim(), null);
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Failed to generate voice design prompt");
                broadcaster?.Publish(new StreamFailed(ex.Message));
                return new GenerateResult(GenerateStatus.Failed, null, ex.Message);
            }
        }
    }
}
