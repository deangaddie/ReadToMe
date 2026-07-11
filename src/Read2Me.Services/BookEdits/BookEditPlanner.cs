using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;

namespace Read2Me.Services.BookEdits
{
    public enum EditPlanStatus { Ok, NoLlmConfigured, Unsupported, Failed, ServiceUnavailable }

    public sealed record EditPlanOutcome(EditPlanStatus Status, EditProgram? Program, string? Reason);

    /// <summary>
    /// Phase A of the AI book-edit flow: one grammar-constrained LLM call that turns the
    /// user's free-text instruction into a structured <see cref="EditProgram"/>.
    /// </summary>
    public class BookEditPlanner(
        ILlmClient llm,
        LlmSettingsService settings,
        IProjectReader reader,
        ChapterOutlineBuilder outlineBuilder,
        ILogger<BookEditPlanner> logger,
        EventBroadcaster<LlmStreamEvent> broadcaster,
        IAiServiceReporter reporter)
    {
        public virtual async Task<EditPlanOutcome> PlanAsync(
            ProjectFolderId folderId, string instruction, CancellationToken ct)
        {
            LlmServerConfig? config = null;
            try
            {
                config = await settings.GetActiveConfigAsync();
                if (config == null)
                    return new EditPlanOutcome(EditPlanStatus.NoLlmConfigured, null,
                        "No active LLM server configured");

                var project = await reader.GetProjectAsync(folderId);
                var outline = await outlineBuilder.BuildAsync(folderId, ct);

                var prompt = PromptTemplates.Render(PromptTemplates.DefaultEditPlanPrompt, new Dictionary<string, string>
                {
                    [PromptTemplates.BookTitle]      = project?.BookTitle ?? string.Empty,
                    [PromptTemplates.BookAuthor]     = project?.Author ?? string.Empty,
                    [PromptTemplates.Instruction]    = instruction,
                    [PromptTemplates.BookOutline]    = outline,
                    [PromptTemplates.ResponseFormat] = EditProgramSchema.JsonExample,
                });

                logger.LogDebug("Sending edit-plan prompt for instruction: {Instruction}", instruction);

                broadcaster.Publish(new RequestStarted(instruction, prompt));
                var metrics = new StreamMetrics(prompt);
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder();
                var scanner = JsonCompletionScanner.ForObject();
                await foreach (var chunk in llm.StreamChatAsync(config, prompt, EditProgramSchema.JsonSchema, ct))
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
                if (!EditProgramParser.TryParse(raw, out var program, out var error))
                {
                    var reason = $"{error} Response: {raw[..Math.Min(200, raw.Length)]}";
                    logger.LogWarning("Failed to parse edit plan: {Reason}", reason);
                    broadcaster.Publish(new StreamFailed(reason));
                    return new EditPlanOutcome(EditPlanStatus.Failed, null, reason);
                }

                if (!program!.Supported)
                {
                    logger.LogInformation("Edit plan unsupported: {Reason}", program.UnsupportedReason);
                    return new EditPlanOutcome(EditPlanStatus.Unsupported, program,
                        program.UnsupportedReason ?? "The instruction is outside what AI edits can change.");
                }

                logger.LogInformation("Edit plan parsed: target={Target}, transform={Kind}",
                    program.Target, program.Transform.Kind);
                return new EditPlanOutcome(EditPlanStatus.Ok, program, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error planning book edit");
                broadcaster.Publish(new StreamFailed(ex.Message));

                var reported = config is not null && reporter.ReportFailure(config.BaseUrl, ex);
                return new EditPlanOutcome(
                    reported ? EditPlanStatus.ServiceUnavailable : EditPlanStatus.Failed,
                    null, ex.Message);
            }
        }
    }
}
