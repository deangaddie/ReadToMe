using Read2Me.Core.Models;
using Read2Me.Services.History;

namespace Read2Me.Services.BookEdits
{
    /// <summary>What asking for an Edit plan answered.</summary>
    public abstract record EditPlanResult
    {
        private EditPlanResult() { }

        /// <summary>A Restore was running, or began after planning started (even one that has ended).</summary>
        public sealed record Refused(ActivityRefusal Refusal) : EditPlanResult;

        /// <summary>
        /// The planner's own non-<c>Ok</c> status and reason, or <see cref="EditPlanStatus.NoTargets"/>
        /// when the plan parsed but matched nothing. No session is kept.
        /// </summary>
        public sealed record NotPlanned(EditPlanStatus Status, string? Reason) : EditPlanResult;

        /// <summary>
        /// The Edit session now held for the plan. <paramref name="RequestCount"/> is the LLM requests its
        /// proposal will send, 0 for a deterministic transform.
        /// </summary>
        public sealed record Planned(BookEditSession Session, int RequestCount, IReadOnlyList<string> Warnings) : EditPlanResult;
    }

    /// <summary>
    /// The <b>Edit plan</b> use case: one instruction becomes a program and the items it matches, held
    /// as an <b>Edit session</b>. Owns the Restore race whole — refused while a Restore runs, and when
    /// one began at any point after planning started, since its targets would predate it; a session
    /// created before the Restore showed is removed again.
    /// </summary>
    public class BookEditPlanService(
        BookEditPlanner planner,
        ScopeResolver resolver,
        IBookEditSessionStore sessions,
        ProjectActivity activity)
    {
        /// <summary>Above this many LLM-edited items the plan warns that the job is large.</summary>
        public const int LlmTargetWarnThreshold = 300;

        private const string NoTargetsReason = "No items in the book match this instruction's scope.";

        /// <param name="thinking">Let the model think before planning.</param>
        public async Task<EditPlanResult> PlanAsync(
            ProjectFolderId folder, string instruction, bool thinking, CancellationToken ct)
        {
            var mark = activity.Mark(folder);
            // A Restore already running is counted in the mark, so only its flag shows it: refuse it
            // here, or the check below would pass once it ended and leave targets read while it ran.
            if (activity.MayPlanEdit(mark) is { } refusal)
                return new EditPlanResult.Refused(refusal);

            var outcome = await planner.PlanAsync(folder, instruction, !thinking, ct);
            if (outcome.Status != EditPlanStatus.Ok)
                return new EditPlanResult.NotPlanned(outcome.Status, outcome.Reason);

            var program = outcome.Program!;
            var targets = await resolver.ResolveAsync(folder, program, ct);
            if (targets.Count == 0)
                return new EditPlanResult.NotPlanned(EditPlanStatus.NoTargets, NoTargetsReason);

            var session = sessions.Create(folder, program, targets, EditProgramDescriber.Describe(program));
            // A Restore that began while the LLM was thinking or the scope resolved drops the project's
            // sessions as it ends, or has ended and left targets read from the book before it; the
            // client, holding no program yet, would miss either: refuse as the Restore would have.
            if (activity.MayPlanEdit(mark) is { } overtaken)
            {
                sessions.Remove(folder, session.Id);
                return new EditPlanResult.Refused(overtaken);
            }

            // The run packs with the same function, so the count cannot drift from the job.
            var requestCount = session.IsLlmTransform ? BatchPacker.RequestCount(targets) : 0;
            var warnings = new List<string>();
            if (session.IsLlmTransform && targets.Count > LlmTargetWarnThreshold)
                warnings.Add($"This is a large AI job ({targets.Count} items). It may take a long time.");
            if (EditProgramDescriber.TitlesAlsoMatch(await resolver.CountTitlesAlsoMatchingAsync(folder, program, ct)) is { } titles)
                warnings.Add(titles);

            return new EditPlanResult.Planned(session, requestCount, warnings);
        }
    }
}
