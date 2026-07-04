using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    public enum AttributionStatus { Resolved, Unknown, NoLlmConfigured, Failed, ServiceUnavailable }

    public sealed record AttributionOutcome(
        AttributionStatus Status,
        string? Character,
        string? VoiceInstructions,
        string? FailureReason);

    public class CharacterAttributionService(
        ILlmClient llm,
        LlmSettingsService settings,
        LlmPromptService prompts,
        IProjectReader reader,
        ILogger<CharacterAttributionService> logger,
        EventBroadcaster<LlmStreamEvent> broadcaster,
        IAiServiceReporter reporter)
    {
        public virtual async Task<AttributionOutcome> AttributeAsync(QueuedParagraph item, CancellationToken ct)
        {
            LlmServerConfig? config = null;
            try
            {
                config = await settings.GetActiveConfigAsync();
                if (config == null)
                {
                    logger.LogWarning("No active LLM config — skipping paragraph {ParagraphId}", item.ParagraphId);
                    return new AttributionOutcome(AttributionStatus.NoLlmConfigured, null, null,
                        "No active LLM server configured");
                }

                var (before, after) = await prompts.GetContextWindowAsync();

                var ctx = await reader.GetParagraphContextAsync(
                    item.Folder, item.ChapterId, item.ParagraphId, before, after);

                if (ctx == null || string.IsNullOrWhiteSpace(ctx.Query.Text))
                {
                    logger.LogInformation("Paragraph {ParagraphId} has no text — marking unknown", item.ParagraphId);
                    return new AttributionOutcome(AttributionStatus.Unknown, null, null, null);
                }

                var project = await reader.GetProjectAsync(item.Folder);
                var characters = await reader.GetCharactersWithAliasesAsync(item.Folder);
                var characterNames = characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() });

                var template = await prompts.GetCharacterPromptAsync();
                var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
                {
                    [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                    [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                    [PromptTemplates.KnownCharacters] = JsonSerializer.Serialize(characterNames),
                    [PromptTemplates.ContextJson]     = PromptTemplates.BuildContextJson(ctx),
                    [PromptTemplates.ResponseFormat]  = CharacterAttributionSchema.JsonExample,
                });

                logger.LogDebug("Sending character attribution prompt for paragraph {ParagraphId}", item.ParagraphId);

                broadcaster.Publish(new RequestStarted(item.Preview, prompt));
                var metrics = new StreamMetrics(prompt);
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder();
                await foreach (var chunk in llm.StreamChatAsync(config, prompt, ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } c)
                    {
                        sb.Append(c);
                        metrics.AddOutput(c);
                        broadcaster.Publish(new ContentDelta(c));
                    }
                }
                sw.Stop();
                broadcaster.Publish(new StreamCompleted(metrics.TokensIn, metrics.TokensOut,
                    sw.Elapsed.TotalSeconds, metrics.TokensPerSecond(sw.Elapsed.TotalSeconds)));

                // Stream completed against a managed service — clear its failure streak.
                reporter.ReportSuccess(config.BaseUrl);

                var raw = sb.ToString();

                if (!CharacterAttributionParser.TryParse(raw, out var parsed))
                {
                    var reason = $"Could not parse LLM response: {raw[..Math.Min(200, raw.Length)]}";
                    logger.LogWarning("Failed to parse LLM response for {ParagraphId}: {Raw}", item.ParagraphId, raw);
                    broadcaster.Publish(new StreamFailed(reason));
                    return new AttributionOutcome(AttributionStatus.Failed, null, null, reason);
                }

                if (string.IsNullOrWhiteSpace(parsed.Character) ||
                    parsed.Character.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("LLM returned unknown for paragraph {ParagraphId}", item.ParagraphId);
                    return new AttributionOutcome(AttributionStatus.Unknown, null, null, null);
                }

                logger.LogInformation("LLM attributed paragraph {ParagraphId} to '{Character}'",
                    item.ParagraphId, parsed.Character);
                return new AttributionOutcome(AttributionStatus.Resolved,
                    parsed.Character, parsed.VoiceInstructions, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Genuine cancel (CancelAll / host shutdown) — surface as today.
                throw;
            }
            catch (Exception ex)
            {
                // Everything else — including a client timeout (TaskCanceledException with ct not
                // cancelled) and an AiServiceStalledException — is a request failure. If it was
                // against a managed docker service, report it and surface ServiceUnavailable so the
                // processor requeues instead of failing; a remote endpoint behaves exactly as before.
                logger.LogError(ex, "Error attributing paragraph {ParagraphId}", item.ParagraphId);
                broadcaster.Publish(new StreamFailed(ex.Message));

                var reported = config is not null && reporter.ReportFailure(config.BaseUrl, ex);
                return new AttributionOutcome(
                    reported ? AttributionStatus.ServiceUnavailable : AttributionStatus.Failed,
                    null, null, ex.Message);
            }
        }
    }
}
