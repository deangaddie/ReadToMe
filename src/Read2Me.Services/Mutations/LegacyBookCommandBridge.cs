namespace Read2Me.Services.Mutations;

/// <summary>
/// Lets a family that has already migrated to <see cref="BookMutations"/> keep serving the callers
/// that still hold <see cref="IBookCommandHandler"/>, by flattening a typed outcome back into the
/// legacy <c>Guid?</c> contract.
/// <para>
/// The flattening is lossy on purpose, because <c>POST /api/projects/{folder}/commands</c> keeps its
/// existing responses through the migration. A command naming a node the Book does not contain
/// no-ops there rather than rejecting — <c>docs/agents/api.md</c> calls that out as what makes
/// <c>SetNarratorCharacter</c> unusual — so <see cref="BookMutationRejection.NotFound"/> flattens to
/// null beside <see cref="BookMutationOutcome.NoChange"/>, and only a genuine domain refusal throws
/// into the endpoint's 422. Callers that need the distinction read the outcome directly.
/// </para>
/// <para>
/// This bridge is scaffolding with a known end. The final contraction ticket deletes it together
/// with the legacy façade, once no caller remains that cannot read an outcome.
/// </para>
/// </summary>
public static class LegacyBookCommandBridge
{
    public static async Task<Guid?> ExecuteLegacyAsync(
        this BookMutations mutations, BookMutation mutation, CancellationToken ct)
    {
        var outcome = await mutations.CommitAsync(mutation, ct);
        return outcome switch
        {
            BookMutationOutcome.Committed committed => committed.Receipt.Effects.CreatedId,
            BookMutationOutcome.NoChange => null,
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.NotFound } => null,
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled
                => throw new OperationCanceledException(cancelled.Message, ct),
            BookMutationOutcome.Rejected rejected => throw new InvalidOperationException(rejected.Message),
            // Unreachable — the hierarchy is closed by a private constructor — but C# cannot prove it.
            _ => throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}."),
        };
    }
}
