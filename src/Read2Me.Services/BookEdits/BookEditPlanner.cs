using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
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
        ILlmCompletionRunner runner,
        LlmSettingsService settings,
        IProjectReader reader,
        ChapterOutlineBuilder outlineBuilder,
        ILogger<BookEditPlanner> logger)
    {
        /// <param name="disableThinking">
        /// Skip the model's hidden thinking phase for this plan call. Thinking helps on tricky or
        /// ambiguous instructions and costs most of the generation time, so the user chooses per run.
        /// </param>
        public virtual async Task<EditPlanOutcome> PlanAsync(
            ProjectFolderId folderId, string instruction, bool disableThinking, CancellationToken ct)
        {
            var config = await settings.GetActiveConfigAsync();
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

            var result = await runner.RunAsync<EditProgram>(
                new LlmRunRequest(config, prompt, instruction,
                    EditProgramSchema.JsonSchema, CompletionShape.Object, disableThinking),
                EditProgramParser.TryParse, ct);

            switch (result.Outcome)
            {
                case LlmRunOutcome.Completed:
                    break;
                case LlmRunOutcome.ServiceUnavailable:
                    return new EditPlanOutcome(EditPlanStatus.ServiceUnavailable, null, result.Error);
                default:
                    return new EditPlanOutcome(EditPlanStatus.Failed, null, result.Error);
            }

            var program = result.Value!;
            if (!program.Supported)
            {
                logger.LogInformation("Edit plan unsupported: {Reason}", program.UnsupportedReason);
                return new EditPlanOutcome(EditPlanStatus.Unsupported, program,
                    program.UnsupportedReason ?? "The instruction is outside what AI edits can change.");
            }

            logger.LogInformation("Edit plan parsed: target={Target}, transform={Kind}",
                program.Target, program.Transform.Kind);
            return new EditPlanOutcome(EditPlanStatus.Ok, program, null);
        }
    }
}
