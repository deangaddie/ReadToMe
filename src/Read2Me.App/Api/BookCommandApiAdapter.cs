using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Maps what a Book mutation did onto the answers <c>POST /api/projects/{folder}/commands</c>
    /// has always given. This is the whole of the endpoint's outcome handling: the contract itself
    /// — discriminators, request bodies, <c>newEntityId</c>, and which conditions are 4xx — is
    /// unchanged by ADR 0007, and nothing below the adapter knows about HTTP.
    /// <para>
    /// The map is deliberately complete and command-agnostic. Which refusals a particular command
    /// has always answered as <c>200 { "newEntityId": null }</c> is decided one layer down, in the
    /// handler that translates that command (see <see cref="BookCommandWireContract"/>), so this
    /// never infers wire behaviour from a command's name.
    /// </para>
    /// </summary>
    public sealed class BookCommandApiAdapter(BookCommandDispatcher dispatcher)
    {
        public async Task<Results<Ok<CommandResponse>, NotFound, ProblemHttpResult>> ExecuteAsync(BookCommand command, CancellationToken ct)
        {
            try
            {
                return ToResult(await dispatcher.ExecuteAsync(command, ct), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An unexpected implementation defect. It is not an expected outcome and must not
                // be dressed as one, but the endpoint has always reported it as 422 rather than
                // failing the request outright, and that stays.
                return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        /// <summary>
        /// The mapping itself, separated so it can be read — and tested — as one table. It never
        /// answers <see cref="NotFound"/>; the union carries it so the endpoint's unknown-project 404
        /// is the same return type.
        /// </summary>
        public static Results<Ok<CommandResponse>, NotFound, ProblemHttpResult> ToResult(BookCommandResult result, CancellationToken ct) =>
            result.Outcome switch
            {
                // A committed mutation answers with the identity the command has always reported —
                // the created one for most, a resolved one for CreateCharacter, none for the two
                // that have never reported one.
                BookMutationOutcome.Committed => TypedResults.Ok(new CommandResponse(result.EntityId)),

                // Nothing changed: no revision, no receipt, and nothing for a Book View to
                // reconcile. On the wire that has always been success with no id — which is also
                // where a refusal this command has always answered as null arrives.
                BookMutationOutcome.NoChange => TypedResults.Ok(new CommandResponse(result.EntityId)),

                // Every other refusal answers by its Refusal kind (MutationRefusalProblem): a cancelled
                // one rethrows, contention — a Restore begun after the endpoint's own check, another
                // writer, a linked book that can't take the write (ADR 0018) — is 409. One answer is
                // this contract's own (ADR 0007): a missing thing a command did not soften stays the
                // 422 an agent has always seen, since the endpoint's 404 means an unknown project.
                BookMutationOutcome.Rejected rejected
                    => MutationRefusalProblem.From(rejected, ct, missingStatus: StatusCodes.Status422UnprocessableEntity),

                _ => throw new NotSupportedException(
                    $"Unhandled mutation outcome {result.Outcome.GetType().Name}."),
            };
    }
}
