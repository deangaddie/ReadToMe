using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Llm;

namespace Read2Me.Services.BookEdits
{
    public enum ProposalStatus { Proposed, NoChange, Failed }

    public sealed record ProposedEdit(
        BookEditTargetKind Kind,
        Guid Id,
        string DisplayPath,
        string OldValue,
        string? NewValue,
        ProposalStatus Status,
        string? FailureReason)
    {
        public BookEditItem ToEditItem() => new(Kind, Id, NewValue!);
    }

    /// <summary>
    /// Phase B of the AI book-edit flow: computes proposed new values for resolved targets.
    /// Deterministic transforms run in code; "llm" transforms run as batched,
    /// grammar-constrained LLM calls. Cancellation returns the proposals computed so far.
    /// </summary>
    public class BookEditProposalService(
        ILlmCompletionRunner runner,
        LlmSettingsService settings,
        IProjectCatalogReader catalog,
        ILogger<BookEditProposalService> logger)
    {
        private const int BatchSize = 8;

        public virtual async Task<IReadOnlyList<ProposedEdit>> ProposeAsync(
            ProjectFolderId folderId,
            EditProgram program,
            IReadOnlyList<EditTarget> targets,
            IProgress<(int Done, int Total)>? progress,
            CancellationToken ct)
        {
            return program.Transform.Kind == TransformKind.Llm
                ? await ProposeWithLlmAsync(folderId, program, targets, progress, ct)
                : ProposeDeterministic(program, targets, progress);
        }

        private static IReadOnlyList<ProposedEdit> ProposeDeterministic(
            EditProgram program, IReadOnlyList<EditTarget> targets, IProgress<(int, int)>? progress)
        {
            var proposals = new List<ProposedEdit>(targets.Count);
            foreach (var target in targets)
            {
                string? newValue = null;
                string? failure = null;
                try
                {
                    newValue = program.Transform.Kind switch
                    {
                        TransformKind.RegexReplace => DeterministicTransformer.RegexReplace(
                            target.CurrentValue, program.Transform.Pattern!, program.Transform.Replacement),
                        _ => DeterministicTransformer.RenderTemplate(
                            program.Transform.Template!, target.OrdinalInScope, target.CurrentValue),
                    };
                }
                catch (RegexMatchTimeoutException)
                {
                    failure = "Pattern timed out on this text.";
                }
                proposals.Add(Build(target, newValue, failure));
            }
            progress?.Report((targets.Count, targets.Count));
            return proposals;
        }

        private async Task<IReadOnlyList<ProposedEdit>> ProposeWithLlmAsync(
            ProjectFolderId folderId,
            EditProgram program,
            IReadOnlyList<EditTarget> targets,
            IProgress<(int, int)>? progress,
            CancellationToken ct)
        {
            var proposals = new List<ProposedEdit>(targets.Count);

            var config = await settings.GetActiveConfigAsync();
            if (config == null)
            {
                foreach (var target in targets)
                    proposals.Add(Build(target, null, "No active LLM server configured"));
                return proposals;
            }

            var project = await catalog.GetProjectAsync(folderId);
            var instruction = program.Transform.Instruction!;

            for (var offset = 0; offset < targets.Count; offset += BatchSize)
            {
                if (ct.IsCancellationRequested)
                    return proposals; // partial set stays reviewable

                var batch = targets.Skip(offset).Take(BatchSize).ToList();
                LlmRunResult<IReadOnlyDictionary<int, string>> run;
                try
                {
                    run = await RunBatchAsync(config, project, instruction, batch, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return proposals; // partial set stays reviewable
                }

                if (run.Outcome is LlmRunOutcome.Failed or LlmRunOutcome.ServiceUnavailable)
                {
                    logger.LogError("Batch edit request failed at offset {Offset}: {Reason}", offset, run.Error);

                    // Service likely down — fail every remaining target instead of retrying batch after batch.
                    foreach (var target in targets.Skip(offset))
                        proposals.Add(Build(target, null, run.Error));
                    progress?.Report((targets.Count, targets.Count));
                    return proposals;
                }

                var results = run.Outcome == LlmRunOutcome.Completed ? run.Value : null;
                for (var i = 0; i < batch.Count; i++)
                {
                    proposals.Add(results != null && results.TryGetValue(i, out var newText)
                        ? Build(batch[i], newText, null)
                        : Build(batch[i], null, "The AI response did not include this item."));
                }

                progress?.Report((proposals.Count, targets.Count));
            }

            return proposals;
        }

        private Task<LlmRunResult<IReadOnlyDictionary<int, string>>> RunBatchAsync(
            LlmServerConfig config, Read2Me.Data.Entities.Project? project,
            string instruction, IReadOnlyList<EditTarget> batch, CancellationToken ct)
        {
            var itemsJson = PromptTemplates.BuildEditItemsJson(
                batch.Select((t, i) => (i, t.DisplayPath, t.CurrentValue)));

            var prompt = PromptTemplates.Render(PromptTemplates.DefaultBatchEditPrompt, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]      = project?.BookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]     = project?.Author ?? string.Empty,
                [PromptTemplates.Instruction]    = instruction,
                [PromptTemplates.EditItemsJson]  = itemsJson,
                [PromptTemplates.ResponseFormat] = BookEditBatchSchema.JsonExample,
            });

            return runner.RunAsync<IReadOnlyDictionary<int, string>>(
                new LlmRunRequest(config, prompt, $"{batch.Count} edit(s): {batch[0].DisplayPath}",
                    BookEditBatchSchema.JsonSchema, CompletionShape.Array),
                TryParseBatch, ct);
        }

        private static bool TryParseBatch(
            string raw, out IReadOnlyDictionary<int, string>? results, out string? error)
        {
            if (BookEditBatchParser.TryParse(raw, out var parsed))
            {
                results = parsed;
                error = null;
                return true;
            }
            results = null;
            error = "Could not parse batch edit response.";
            return false;
        }

        private static ProposedEdit Build(EditTarget target, string? newValue, string? failure)
        {
            if (failure != null)
                return new ProposedEdit(target.Kind, target.Id, target.DisplayPath,
                    target.CurrentValue, null, ProposalStatus.Failed, failure);
            var status = newValue == target.CurrentValue ? ProposalStatus.NoChange : ProposalStatus.Proposed;
            return new ProposedEdit(target.Kind, target.Id, target.DisplayPath,
                target.CurrentValue, newValue, status, null);
        }
    }
}
