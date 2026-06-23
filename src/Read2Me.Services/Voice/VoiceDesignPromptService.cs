using System.Text;
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
        private readonly ILlmClient llm;
        private readonly LlmSettingsService settings;
        private readonly LlmPromptService prompts;
        private readonly ILogger<VoiceDesignPromptService> logger;

        public VoiceDesignPromptService(
            ILlmClient llm,
            LlmSettingsService settings,
            LlmPromptService prompts,
            ILogger<VoiceDesignPromptService> logger)
        {
            this.llm = llm;
            this.settings = settings;
            this.prompts = prompts;
            this.logger = logger;
            if (prompts != null)
                prompts.OnChanged += () => _cachedVoicePromptTemplate = null;
        }

        public enum GenerateStatus { Success, NoLlmConfigured, Failed }

        public sealed record GenerateResult(GenerateStatus Status, string? Prompt, string? FailureReason);

        private string? _cachedVoicePromptTemplate;

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

                var sb = new StringBuilder();
                await foreach (var chunk in llm.StreamChatAsync(config, rendered, ct))
                {
                    if (chunk.Content is { } delta)
                        sb.Append(delta);
                }

                return new GenerateResult(GenerateStatus.Success, sb.ToString().Trim(), null);
            }
            catch (System.Exception ex)
            {
                logger.LogError(ex, "Failed to generate voice design prompt");
                return new GenerateResult(GenerateStatus.Failed, null, ex.Message);
            }
        }
    }
}
