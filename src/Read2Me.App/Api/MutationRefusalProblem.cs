using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Api
{
    /// <summary>
    /// The one HTTP answer to a refused Book mutation, by its Refusal kind
    /// (<c>docs/agents/api/README.md</c>, "Refused mutations"): contention 409, missing 404,
    /// invalid 422, and a cancelled one never answers — it is rethrown, so ASP.NET's abort handling
    /// runs rather than an invented status. Every endpoint that commits a mutation answers through
    /// here; the command endpoint's two older answers are its own (<see cref="BookCommandApiAdapter"/>).
    /// </summary>
    public static class MutationRefusalProblem
    {
        /// <summary>The status a refusal of this kind answers with; null for a cancelled one, which never answers.</summary>
        public static int? StatusOf(BookMutationRefusalKind kind) => kind switch
        {
            BookMutationRefusalKind.Cancelled => null,
            BookMutationRefusalKind.Contention => StatusCodes.Status409Conflict,
            BookMutationRefusalKind.Missing => StatusCodes.Status404NotFound,
            BookMutationRefusalKind.Invalid => StatusCodes.Status422UnprocessableEntity,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped refusal kind."),
        };

        /// <summary>
        /// The problem a refusal answers with, carrying its message and extensions (a Blocked
        /// refusal's <c>books</c>, a gate refusal's <c>activity</c>). Throws
        /// <see cref="OperationCanceledException"/> for a cancelled one.
        /// </summary>
        /// <param name="missingStatus">An endpoint whose contract predates the table and answers a missing thing otherwise.</param>
        public static ProblemHttpResult From(BookMutationOutcome.Rejected refusal, CancellationToken ct, int? missingStatus = null)
        {
            refusal.ThrowIfCancelled(ct);
            // Refused by the Project activity gate: the same 409 the gate's endpoint filter answers,
            // with the `activity` extension naming the state that refused it.
            if (refusal.Activity is { } activity)
                return StepReads.Refused(activity);
            var status = refusal.Kind == BookMutationRefusalKind.Missing && missingStatus is { } legacy
                ? legacy
                : StatusOf(refusal.Kind)!.Value;
            return TypedResults.Problem(refusal.Message, statusCode: status,
                extensions: refusal.Extensions?.ToDictionary(e => e.Key, e => e.Value));
        }
    }
}
