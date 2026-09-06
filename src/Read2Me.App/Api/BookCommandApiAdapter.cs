using Microsoft.AspNetCore.Http;
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
        public async Task<IResult> ExecuteAsync(BookCommand command, CancellationToken ct)
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
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        }

        /// <summary>
        /// The mapping itself, separated so it can be read — and tested — as one table.
        /// </summary>
        public static IResult ToResult(BookCommandResult result, CancellationToken ct) =>
            result.Outcome switch
            {
                // A committed mutation answers with the identity the command has always reported —
                // the created one for most, a resolved one for CreateCharacter, none for the two
                // that have never reported one.
                BookMutationOutcome.Committed => Results.Ok(new CommandResponse(result.EntityId)),

                // Nothing changed: no revision, no receipt, and nothing for a Book View to
                // reconcile. On the wire that has always been success with no id — which is also
                // where a refusal this command has always answered as null arrives.
                BookMutationOutcome.NoChange => Results.Ok(new CommandResponse(result.EntityId)),

                // Cancelled before the commit point, so nothing was written. The request is already
                // gone; rethrowing keeps ASP.NET's abort handling rather than inventing a status.
                // A mutation that has committed never reaches here: BookMutations stops observing
                // cancellation at its commit point precisely so a committed change cannot be
                // reported as an uncommitted one.
                BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled
                    => throw new OperationCanceledException(cancelled.Message, ct),

                // Every other expected refusal the command did not soften: the same 422 an agent has
                // always seen, now carrying the mutation's own reason as the message.
                BookMutationOutcome.Rejected rejected
                    => Results.Problem(rejected.Message, statusCode: StatusCodes.Status422UnprocessableEntity),

                _ => throw new NotSupportedException(
                    $"Unhandled mutation outcome {result.Outcome.GetType().Name}."),
            };
    }
}
