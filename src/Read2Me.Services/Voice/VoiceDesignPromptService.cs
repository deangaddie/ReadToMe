using Microsoft.Extensions.Logging;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Voice
{
    /// <summary>
    /// Generates a voice-design text prompt by calling the LLM with the configured
    /// voice prompt template, substituting book/author/character details.
    /// </summary>
    public class VoiceDesignPromptService
    {
        private readonly ILlmCompletionRunner runner;
        private readonly LlmSettingsService settings;
        private readonly LlmPromptService prompts;
        private readonly ILogger<VoiceDesignPromptService> logger;

        public VoiceDesignPromptService(
            ILlmCompletionRunner runner,
            LlmSettingsService settings,
            LlmPromptService prompts,
            ILogger<VoiceDesignPromptService> logger)
        {
            this.runner = runner;
            this.settings = settings;
            this.prompts = prompts;
            this.logger = logger;
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

            var rendered = await BuildRenderedPlanPromptAsync(bookTitle, author, characterName, isNarrator);

            var result = await runner.RunAsync<IReadOnlyList<VoicePlanVoice>>(
                new LlmRunRequest(config, rendered, $"Voice plan: {characterName}",
                    VoicePlanSchema.JsonSchema, CompletionShape.Array),
                TryParseVoicePlan, ct);

            return result.Outcome == LlmRunOutcome.Completed
                ? new PlanResult(GenerateStatus.Success, result.Value, null)
                : new PlanResult(GenerateStatus.Failed, null, result.Error);
        }

        private static bool TryParseVoicePlan(
            string raw, out IReadOnlyList<VoicePlanVoice>? voices, out string? error)
        {
            if (VoicePlanParser.TryParse(raw, out var parsed))
            {
                voices = parsed;
                error = null;
                return true;
            }
            voices = null;
            error = "LLM response was not a valid voice-plan JSON array.";
            return false;
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

            // Thinking off: a voice prompt is creative writing with no recall pressure — measured
            // output quality holds without thinking at a fraction of the time. Voice plans above
            // keep thinking: choosing change points across a published book is recall-heavy.
            var result = await runner.RunAsync(
                new LlmRunRequest(config, renderedPrompt, "Voice prompt", Shape: CompletionShape.None,
                    DisableThinking: true), ct);

            return result.Outcome == LlmRunOutcome.Completed
                ? new GenerateResult(GenerateStatus.Success, result.Value!.Trim(), null)
                : new GenerateResult(GenerateStatus.Failed, null, result.Error);
        }
    }
}
