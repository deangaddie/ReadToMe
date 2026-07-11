using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
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
    /// chain — asking for the book's notable characters and their aliases. Modelled on
    /// <see cref="BookEdits.BookEditPlanner"/>: same live-stream event publishing, the same
    /// early-stop JSON completion scan, and the same mapping of an infrastructure failure to
    /// <see cref="DiscoveryStatus.ServiceUnavailable"/> via the AI service reporter.
    /// </summary>
    public class CharacterDiscoveryService(
        ILlmClient llm,
        LlmSettingsService settings,
        IProjectReader reader,
        ChapterOutlineBuilder outlineBuilder,
        LlmPromptService prompts,
        ILogger<CharacterDiscoveryService> logger,
        EventBroadcaster<LlmStreamEvent> broadcaster,
        IAiServiceReporter reporter)
    {
        public virtual async Task<DiscoveryOutcome> DiscoverAsync(
            ProjectFolderId folderId, CancellationToken ct)
        {
            LlmServerConfig? config = null;
            try
            {
                config = await settings.GetActiveConfigAsync();
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

                broadcaster.Publish(new RequestStarted("Discover characters", prompt));
                var metrics = new StreamMetrics(prompt);
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder();
                var scanner = JsonCompletionScanner.ForObject();
                await foreach (var chunk in llm.StreamChatAsync(config, prompt, CharacterDiscoverySchema.JsonSchema, ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } c)
                    {
                        sb.Append(c);
                        metrics.AddOutput(c);
                        broadcaster.Publish(new ContentDelta(c));
                        if (scanner.Append(c))
                            break;
                    }
                }
                sw.Stop();
                broadcaster.Publish(new StreamCompleted(metrics.TokensIn, metrics.TokensOut,
                    sw.Elapsed.TotalSeconds, metrics.TokensPerSecond(sw.Elapsed.TotalSeconds)));

                reporter.ReportSuccess(config.BaseUrl);

                var raw = sb.ToString();
                if (!CharacterDiscoveryParser.TryParse(raw, out var discovered, out var error))
                {
                    var reason = $"{error} Response: {raw[..Math.Min(200, raw.Length)]}";
                    logger.LogWarning("Failed to parse discovery response: {Reason}", reason);
                    broadcaster.Publish(new StreamFailed(reason));
                    return new DiscoveryOutcome(DiscoveryStatus.Failed, [], reason);
                }

                logger.LogInformation("Character discovery parsed {Count} character(s)", discovered.Count);
                return new DiscoveryOutcome(DiscoveryStatus.Ok, discovered, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error discovering characters");
                broadcaster.Publish(new StreamFailed(ex.Message));

                var reported = config is not null && reporter.ReportFailure(config.BaseUrl, ex);
                return new DiscoveryOutcome(
                    reported ? DiscoveryStatus.ServiceUnavailable : DiscoveryStatus.Failed,
                    [], ex.Message);
            }
        }
    }
}
