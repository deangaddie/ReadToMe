using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    public enum DiscoveryStatus { Ok, NoLlmConfigured, Failed, ServiceUnavailable }

    public sealed record DiscoveryOutcome(
        DiscoveryStatus Status,
        IReadOnlyList<DiscoveredCharacter> Characters,
        string? Reason);

    /// <summary>
    /// One grammar-constrained request to the general (active) LLM — never the attribution
    /// chain — asking for the book's notable characters and their aliases. The completion
    /// runner owns the streaming envelope; this service builds the prompt and maps the four
    /// run outcomes onto <see cref="DiscoveryStatus"/>.
    /// </summary>
    public class CharacterDiscoveryService(
        ILlmCompletionRunner runner,
        LlmSettingsService settings,
        IProjectReader reader,
        ChapterOutlineBuilder outlineBuilder,
        LlmPromptService prompts,
        EventBroadcaster<LlmStreamEvent> stream,
        ILogger<CharacterDiscoveryService> logger)
    {
        public virtual async Task<DiscoveryOutcome> DiscoverAsync(
            ProjectFolderId folderId, CancellationToken ct)
        {
            var config = await settings.GetActiveConfigAsync();
            if (config == null)
                return new DiscoveryOutcome(DiscoveryStatus.NoLlmConfigured, [],
                    "No active LLM server configured");

            var project = await reader.GetProjectAsync(folderId);
            var outline = await outlineBuilder.BuildAsync(folderId, ct);
            var characters = await reader.GetCharactersWithAliasesAsync(folderId);
            var knownJson = JsonSerializer.Serialize(
                characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() }));

            var template = await prompts.GetDiscoverCharactersPromptAsync();
            var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                [PromptTemplates.BookOutline]     = outline,
                [PromptTemplates.KnownCharacters] = knownJson,
                [PromptTemplates.ResponseFormat]  = CharacterDiscoverySchema.JsonExample,
            });

            logger.LogDebug("Sending character-discovery prompt for {Folder}", folderId.Value);

            // Discovery is one request, and one request is a genuine Throughput Run of one.
            // The bracket starts here rather than at the top of the method: the early returns
            // above never reach the LLM, so there is no run to open or close.
            LlmRunResult<IReadOnlyList<DiscoveredCharacter>> result;
            stream.Publish(new RunStarted());
            try
            {
                result = await runner.RunAsync<IReadOnlyList<DiscoveredCharacter>>(
                    new LlmRunRequest(config, prompt, "Discover characters",
                        CharacterDiscoverySchema.JsonSchema, CompletionShape.Object),
                    CharacterDiscoveryParser.TryParse, ct);
            }
            finally
            {
                stream.Publish(new RunEnded());
            }

            switch (result.Outcome)
            {
                case LlmRunOutcome.Completed:
                    logger.LogInformation("Character discovery parsed {Count} character(s)", result.Value!.Count);
                    return new DiscoveryOutcome(DiscoveryStatus.Ok, result.Value, null);
                case LlmRunOutcome.ServiceUnavailable:
                    return new DiscoveryOutcome(DiscoveryStatus.ServiceUnavailable, [], result.Error);
                default:
                    return new DiscoveryOutcome(DiscoveryStatus.Failed, [], result.Error);
            }
        }
    }
}
