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
        IBookContentReader content,
        ILogger<BookEditProposalService> logger)
    {
        private const string MissingItemMessage = "The AI response did not include this item.";
        private const string NoConfigMessage = "No active LLM server configured";
        private const string RunFailed = "The AI request failed.";

        /// <param name="disableThinking">
        /// Skip the model's hidden thinking phase on the per-item edit calls. Ignored by
        /// deterministic transforms, which never reach the LLM.
        /// </param>
        public virtual async Task<IReadOnlyList<ProposedEdit>> ProposeAsync(
            ProjectFolderId folderId,
            EditProgram program,
            IReadOnlyList<EditTarget> targets,
            IProgress<(int Done, int Total)>? progress,
            bool disableThinking,
            CancellationToken ct)
        {
            return program.Transform.Kind == TransformKind.Llm
                ? await ProposeWithLlmAsync(folderId, program, targets, progress, disableThinking, ct)
                : ProposeDeterministic(program, targets, await BuildProtectionAsync(folderId, program, ct), progress);
        }

        /// <summary>The book's names, learned once per run — only sentence and title case keep them.</summary>
        private async Task<ProtectionSet> BuildProtectionAsync(
            ProjectFolderId folderId, EditProgram program, CancellationToken ct)
            => program.Transform is { Kind: TransformKind.ChangeCase, CaseMode: CaseMode.Sentence or CaseMode.Title }
                ? ProtectionSet.Build(await content.GetBookTextAsync(folderId, ct))
                : ProtectionSet.Empty;

        private static IReadOnlyList<ProposedEdit> ProposeDeterministic(
            EditProgram program, IReadOnlyList<EditTarget> targets, ProtectionSet protection,
            IProgress<(int, int)>? progress)
        {
            var proposals = new List<ProposedEdit>(targets.Count);
            foreach (var target in targets)
            {
                string? newValue = null;
                // A missing or self source fails only its row, in place; the rest of the plan runs.
                var failure = target.SourceFailure;
                if (failure != null)
                {
                    proposals.Add(Build(target, null, failure));
                    continue;
                }
                try
                {
                    newValue = program.Transform.Kind switch
                    {
                        TransformKind.RegexReplace => DeterministicTransformer.RegexReplace(
                            target.CurrentValue, program.Transform.Pattern!, program.Transform.Replacement),
                        TransformKind.ChangeCase => DeterministicTransformer.ChangeCase(
                            target.CurrentValue, program.Transform.Pattern, program.Transform.CaseMode!.Value,
                            protection),
                        _ => DeterministicTransformer.RenderTemplate(
                            program.Transform.Template!, target.OrdinalInScope, target.CurrentValue, target.Sources),
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
            bool disableThinking,
            CancellationToken ct)
        {
            var config = await settings.GetActiveConfigAsync();
            if (config == null)
                return targets.Select(t => Build(t, null, BatchPacker.PreflightFailure(t) ?? NoConfigMessage)).ToList();

            var project = await catalog.GetProjectAsync(folderId);
            var instruction = program.Transform.Instruction!;

            // Answers sit at their target's position; rows that fail before any request (a missing
            // source, too long to send) are filled first and never occupy an index in a request.
            var proposals = new ProposedEdit?[targets.Count];
            var plan = BatchPacker.PlanFor(targets);
            for (var i = 0; i < targets.Count; i++)
                if (BatchPacker.PreflightFailure(targets[i]) is { } failure)
                    proposals[i] = Build(targets[i], null, failure);

            IReadOnlyList<ProposedEdit> Answered() => proposals.OfType<ProposedEdit>().ToList();
            var sendable = plan.Batches.Sum(b => b.Count);
            var settled = targets.Count - sendable;
            progress?.Report((settled, targets.Count));

            foreach (var positions in plan.Batches)
            {
                if (ct.IsCancellationRequested)
                    return Answered(); // partial set stays reviewable

                var batch = positions.Select(p => targets[p]).ToList();
                LlmRunResult<IReadOnlyDictionary<int, string>> run;
                try
                {
                    run = await RunBatchAsync(config, project, instruction, batch, disableThinking, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Answered(); // partial set stays reviewable
                }

                if (run.Outcome is LlmRunOutcome.Failed or LlmRunOutcome.ServiceUnavailable)
                {
                    logger.LogError("Batch edit request failed at {Path}: {Reason}", batch[0].DisplayPath, run.Error);

                    // Service likely down — fail every remaining target instead of retrying batch after batch.
                    for (var i = 0; i < targets.Count; i++)
                        proposals[i] ??= Build(targets[i], null, run.Error ?? RunFailed);
                    progress?.Report((targets.Count, targets.Count));
                    return Answered();
                }

                var results = run.Outcome == LlmRunOutcome.Completed ? run.Value : null;
                for (var i = 0; i < batch.Count; i++)
                {
                    proposals[positions[i]] = results != null && results.TryGetValue(i, out var newText)
                        ? Build(batch[i], newText, null)
                        : Build(batch[i], null, MissingItemMessage);
                }

                progress?.Report((proposals.Count(p => p != null), targets.Count));
            }

            return Answered();
        }

        /// <summary>
        /// Re-proposes a single target, optionally steered by a free-text <paramref name="hint"/>
        /// folded into the plan's instruction. LLM plans only — deterministic plans have no
        /// instruction to steer, so they fail fast rather than silently running the LLM.
        /// </summary>
        public virtual async Task<ProposedEdit> ProposeOneAsync(
            ProjectFolderId folderId,
            EditProgram program,
            EditTarget target,
            string? hint,
            bool disableThinking,
            CancellationToken ct)
        {
            if (program.Transform.Kind != TransformKind.Llm)
                return Build(target, null, "Asking the AI again is only available for AI-written plans.");

            // The same failures as the run gives the row, without a request.
            if (BatchPacker.PreflightFailure(target) is { } preflight)
                return Build(target, null, preflight);

            var config = await settings.GetActiveConfigAsync();
            if (config == null)
                return Build(target, null, NoConfigMessage);

            var project = await catalog.GetProjectAsync(folderId);
            var instruction = WithHint(program.Transform.Instruction!, hint);

            LlmRunResult<IReadOnlyDictionary<int, string>> run;
            try
            {
                run = await RunBatchAsync(config, project, instruction, [target], disableThinking, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Build(target, null, "Cancelled before the AI replied.");
            }

            if (run.Outcome is LlmRunOutcome.Failed or LlmRunOutcome.ServiceUnavailable)
            {
                logger.LogError("Single edit request failed for {Path}: {Reason}", target.DisplayPath, run.Error);
                return Build(target, null, run.Error ?? RunFailed);
            }

            var results = run.Outcome == LlmRunOutcome.Completed ? run.Value : null;
            return results != null && results.TryGetValue(0, out var newText)
                ? Build(target, newText, null)
                : Build(target, null, MissingItemMessage);
        }

        private static string WithHint(string instruction, string? hint) =>
            string.IsNullOrWhiteSpace(hint)
                ? instruction
                : $"{instruction}\n\nAdditional guidance from the user: {hint.Trim()}";

        /// <summary>The batch edit prompt for these targets, entries indexed 0..n-1 in order.</summary>
        public static string RenderBatchPrompt(
            string? bookTitle, string? author, string instruction, IReadOnlyList<EditTarget> batch)
        {
            var itemsJson = PromptTemplates.BuildEditItemsJson(batch.Select((t, i) =>
                (i, t.DisplayPath, t.CurrentValue, (IReadOnlyList<KeyValuePair<string, string>>?)t.Sources
                    .Where(s => s.Value != null)
                    .Select(s => new KeyValuePair<string, string>(s.Name, s.Value!)).ToList())));

            // One gloss per source name present in this batch, from the catalogue labels.
            var glosses = batch.SelectMany(t => t.Sources).Select(s => s.Name).Distinct(StringComparer.Ordinal)
                .Select(n => (Name: n, Label: EditSourceCatalog.TryParse(n, out var source) ? EditSourceCatalog.Label(source) : n))
                .ToList();

            return PromptTemplates.Render(PromptTemplates.DefaultBatchEditPrompt, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]      = bookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]     = author ?? string.Empty,
                [PromptTemplates.Instruction]    = instruction,
                [PromptTemplates.EditItemsJson]  = itemsJson,
                [PromptTemplates.EditSourceNotes] = PromptTemplates.BuildEditSourceNotes(glosses),
                [PromptTemplates.ResponseFormat] = BookEditBatchSchema.JsonExample,
            });
        }

        private Task<LlmRunResult<IReadOnlyDictionary<int, string>>> RunBatchAsync(
            LlmServerConfig config, Read2Me.Data.Entities.Project? project,
            string instruction, IReadOnlyList<EditTarget> batch, bool disableThinking,
            CancellationToken ct)
        {
            var prompt = RenderBatchPrompt(project?.BookTitle, project?.Author, instruction, batch);

            return runner.RunAsync<IReadOnlyDictionary<int, string>>(
                new LlmRunRequest(config, prompt, $"{batch.Count} edit(s): {batch[0].DisplayPath}",
                    BookEditBatchSchema.JsonSchema, CompletionShape.Array, disableThinking),
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
