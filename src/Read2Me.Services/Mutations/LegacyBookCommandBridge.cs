namespace Read2Me.Services.Mutations;

/// <summary>
/// Lets a family that has already migrated to <see cref="BookMutations"/> keep serving the callers
/// that still hold <see cref="IBookCommandHandler"/>, by flattening a typed outcome back into the
/// legacy <c>Guid?</c> contract.
/// <para>
/// The flattening is lossy on purpose, because <c>POST /api/projects/{folder}/commands</c> keeps its
/// existing responses through the migration. Which refusals a command has always answered with null
/// differs per command, so each handler names them: a command aimed at a node the Book does not
/// contain no-ops nearly everywhere, and the Character roster commands have always answered a
/// protected-narrator gesture the same way. Only a refusal a command has never had a null answer for
/// throws into the endpoint's 422. Callers that need the distinction read the outcome directly.
/// </para>
/// <para>
/// This bridge is scaffolding with a known end. The final contraction ticket deletes it together
/// with the legacy façade, once no caller remains that cannot read an outcome.
/// </para>
/// </summary>
public static class LegacyBookCommandBridge
{
    /// <summary>The common shape: a command naming something the Book does not contain answers null.</summary>
    public static Task<Guid?> ExecuteLegacyAsync(
        this BookMutations mutations, BookMutation mutation, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(mutation, ct, BookMutationRejection.NotFound);

    /// <summary>
    /// The strict shape: every expected refusal throws, so an agent sees a 422 rather than a
    /// success-shaped null. <c>SetNarratorCharacter</c> is the one command written this way —
    /// <c>docs/agents/api.md</c> calls that out as what makes it unusual.
    /// </summary>
    public static Task<Guid?> ExecuteLegacyStrictAsync(
        this BookMutations mutations, BookMutation mutation, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(mutation, ct, answeredAsNull: []);

    public static async Task<Guid?> ExecuteLegacyAsync(
        this BookMutations mutations,
        BookMutation mutation,
        CancellationToken ct,
        params BookMutationRejection[] answeredAsNull)
    {
        var outcome = await mutations.CommitAsync(mutation, ct);
        return outcome switch
        {
            BookMutationOutcome.Committed committed => committed.Receipt.Effects.CreatedId,
            BookMutationOutcome.NoChange => null,
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled
                => throw new OperationCanceledException(cancelled.Message, ct),
            BookMutationOutcome.Rejected rejected when answeredAsNull.Contains(rejected.Reason) => null,
            BookMutationOutcome.Rejected rejected => throw new InvalidOperationException(rejected.Message),
            // Unreachable — the hierarchy is closed by a private constructor — but C# cannot prove it.
            _ => throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}."),
        };
    }
}
