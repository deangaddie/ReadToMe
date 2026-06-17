using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Voice
{
    /// <summary>
    /// Generates a voice-design text prompt by calling the LLM with the configured
    /// voice prompt template, substituting book/author/character details.
    /// </summary>
    public sealed class VoiceDesignPromptService(
        ILlmClient llm,
        LlmSettingsService settings,
        LlmPromptService prompts,
        ILogger<VoiceDesignPromptService> logger)
    {
        public enum GenerateStatus { Success, NoLlmConfigured, Failed }

        public sealed record GenerateResult(GenerateStatus Status, string? Prompt, string? FailureReason);

        public async Task<GenerateResult> GenerateAsync(
            string bookTitle,
            string author,
            string characterName,
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
                var template = await prompts.GetVoicePromptAsync();
                var rendered = PromptTemplates.Render(template, new Dictionary<string, string>
                {
                    [PromptTemplates.BookTitle]     = bookTitle,
                    [PromptTemplates.BookAuthor]    = author,
                    [PromptTemplates.CharacterName] = characterName,
                });

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
                logger.LogError(ex, "Failed to generate voice design prompt for '{CharacterName}'", characterName);
                return new GenerateResult(GenerateStatus.Failed, null, ex.Message);
            }
        }
    }
}
