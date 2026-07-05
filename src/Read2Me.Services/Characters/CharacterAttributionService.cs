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

    /// <summary>
    /// Result of a multi-paragraph attribution request. <see cref="Deferred"/> holds items trimmed
    /// off the contiguous run (an unassigned character paragraph outside the batch sat between
    /// them); the caller should process them as a fresh batch.
    /// </summary>
    public sealed record BatchAttributionResult(
        IReadOnlyList<(QueuedParagraph Item, AttributionOutcome Outcome)> Outcomes,
        IReadOnlyList<QueuedParagraph> Deferred);

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
                var scanner = JsonCompletionScanner.ForObject();
                await foreach (var chunk in llm.StreamChatAsync(config, prompt, CharacterAttributionSchema.JsonSchema, ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } c)
                    {
                        sb.Append(c);
                        metrics.AddOutput(c);
                        broadcaster.Publish(new ContentDelta(c));
                        // Answer object is closed — stop reading. Breaking disposes the stream,
                        // which cancels the request if the model keeps generating past the JSON.
                        if (scanner.Append(c))
                            break;
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

        /// <summary>
        /// Attributes several queued paragraphs (same chapter, book order) in one LLM request.
        /// A single-item batch delegates to <see cref="AttributeAsync"/> so batch size 1 behaves
        /// exactly like the single-paragraph flow. An unparseable batch response, or an index
        /// missing from an otherwise valid response, falls back to the single-paragraph path for
        /// the affected items.
        /// </summary>
        public virtual async Task<BatchAttributionResult> AttributeBatchAsync(
            IReadOnlyList<QueuedParagraph> batch, CancellationToken ct)
        {
            if (batch.Count == 1)
                return new BatchAttributionResult(
                    [(batch[0], await AttributeAsync(batch[0], ct))], []);

            LlmServerConfig? config = null;
            var outcomes = new List<(QueuedParagraph Item, AttributionOutcome Outcome)>();
            var remaining = new List<QueuedParagraph>(batch);
            var deferred = new List<QueuedParagraph>();
            try
            {
                config = await settings.GetActiveConfigAsync();
                if (config == null)
                {
                    logger.LogWarning("No active LLM config — skipping batch of {Count} paragraphs", batch.Count);
                    var outcome = new AttributionOutcome(AttributionStatus.NoLlmConfigured, null, null,
                        "No active LLM server configured");
                    return new BatchAttributionResult([.. batch.Select(b => (b, outcome))], []);
                }

                var (before, after) = await prompts.GetContextWindowAsync();
                var first = batch[0];

                var ctx = await reader.GetParagraphBatchContextAsync(
                    first.Folder, first.ChapterId, [.. batch.Select(b => b.ParagraphId)], before, after);

                if (ctx == null)
                {
                    // First paragraph not found — let the single path give each item its usual outcome.
                    foreach (var item in batch)
                        outcomes.Add((item, await AttributeAsync(item, ct)));
                    return new BatchAttributionResult(outcomes, []);
                }

                var byId = batch.ToDictionary(b => b.ParagraphId);
                var included = ctx.IncludedIds.Select(id => byId[id]).ToList();
                deferred = [.. ctx.DeferredIds.Select(id => byId[id])];
                remaining = new List<QueuedParagraph>(included);

                if (included.Count == 1)
                {
                    outcomes.Add((included[0], await AttributeAsync(included[0], ct)));
                    return new BatchAttributionResult(outcomes, deferred);
                }

                var project = await reader.GetProjectAsync(first.Folder);
                var characters = await reader.GetCharactersWithAliasesAsync(first.Folder);
                var characterNames = characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() });

                var template = await prompts.GetBatchCharacterPromptAsync();
                var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
                {
                    [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                    [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                    [PromptTemplates.KnownCharacters] = JsonSerializer.Serialize(characterNames),
                    [PromptTemplates.ContextJson]     = PromptTemplates.BuildBatchContextJson(ctx),
                    [PromptTemplates.ResponseFormat]  = CharacterBatchAttributionSchema.JsonExample,
                });

                logger.LogDebug("Sending batch character attribution prompt for {Count} paragraphs", included.Count);

                broadcaster.Publish(new RequestStarted($"{included.Count} paragraphs: {first.Preview}", prompt));
                var metrics = new StreamMetrics(prompt);
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder();
                var scanner = JsonCompletionScanner.ForArray();
                await foreach (var chunk in llm.StreamChatAsync(config, prompt, CharacterBatchAttributionSchema.JsonSchema, ct))
                {
                    if (chunk.Thinking is { } t)
                        broadcaster.Publish(new ThinkingDelta(t));
                    if (chunk.Content is { } c)
                    {
                        sb.Append(c);
                        metrics.AddOutput(c);
                        broadcaster.Publish(new ContentDelta(c));
                        // Answer array is closed — stop reading. Breaking disposes the stream,
                        // which cancels the request if the model keeps generating past the JSON.
                        if (scanner.Append(c))
                            break;
                    }
                }
                sw.Stop();
                broadcaster.Publish(new StreamCompleted(metrics.TokensIn, metrics.TokensOut,
                    sw.Elapsed.TotalSeconds, metrics.TokensPerSecond(sw.Elapsed.TotalSeconds)));

                // Stream completed against a managed service — clear its failure streak.
                reporter.ReportSuccess(config.BaseUrl);

                var raw = sb.ToString();

                if (!CharacterBatchAttributionParser.TryParse(raw, out var parsed))
                {
                    var reason = $"Could not parse batch LLM response: {raw[..Math.Min(200, raw.Length)]}";
                    logger.LogWarning("Failed to parse batch LLM response — falling back to single attribution: {Raw}", raw);
                    broadcaster.Publish(new StreamFailed(reason));

                    foreach (var item in included)
                    {
                        outcomes.Add((item, await AttributeAsync(item, ct)));
                        remaining.Remove(item);
                    }
                    return new BatchAttributionResult(outcomes, deferred);
                }

                for (var i = 0; i < included.Count; i++)
                {
                    var item = included[i];
                    if (!parsed.TryGetValue(i, out var entry))
                    {
                        logger.LogWarning("Batch response missing index {Index} — falling back to single attribution for {ParagraphId}",
                            i, item.ParagraphId);
                        outcomes.Add((item, await AttributeAsync(item, ct)));
                    }
                    else if (entry.Character.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("LLM returned unknown for paragraph {ParagraphId}", item.ParagraphId);
                        outcomes.Add((item, new AttributionOutcome(AttributionStatus.Unknown, null, null, null)));
                    }
                    else
                    {
                        logger.LogInformation("LLM attributed paragraph {ParagraphId} to '{Character}'",
                            item.ParagraphId, entry.Character);
                        outcomes.Add((item, new AttributionOutcome(AttributionStatus.Resolved,
                            entry.Character, entry.VoiceInstructions, null)));
                    }
                    remaining.Remove(item);
                }

                return new BatchAttributionResult(outcomes, deferred);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Same failure semantics as the single path, applied to every item that has no
                // outcome yet. Deferred items are returned untouched; the caller retries them and
                // they hit the same failure (and its requeue handling) individually.
                logger.LogError(ex, "Error attributing batch of {Count} paragraphs", batch.Count);
                broadcaster.Publish(new StreamFailed(ex.Message));

                var reported = config is not null && reporter.ReportFailure(config.BaseUrl, ex);
                var outcome = new AttributionOutcome(
                    reported ? AttributionStatus.ServiceUnavailable : AttributionStatus.Failed,
                    null, null, ex.Message);
                foreach (var item in remaining)
                    outcomes.Add((item, outcome));
                return new BatchAttributionResult(outcomes, deferred);
            }
        }
    }
}
