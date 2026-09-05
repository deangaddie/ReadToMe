using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Turns a mutation outcome into the answer <c>POST /api/projects/{folder}/commands</c> has always
/// given for that command, without losing the outcome itself.
/// <para>
/// The endpoint keeps its existing responses through the migration (ADR 0007), and which refusals a
/// command has always answered with <c>200 { "newEntityId": null }</c> differs per command: a
/// command aimed at a node the Book does not contain no-ops nearly everywhere, the Character roster
/// and Voice Rule commands have always answered a protected-narrator or default-rule gesture the
/// same way, and <see cref="Commands.Handlers.SetNarratorCharacterHandler"/> answers none of them
/// that way. So each handler names its own, here, in the one line that translates its command — and
/// what reaches <c>BookCommandApiAdapter</c> is a result it can map without knowing any command's
/// name.
/// </para>
/// <para>
/// A refusal a command has always answered as null is reported as
/// <see cref="BookMutationOutcome.NoChange"/>, because that is exactly what the wire says about it:
/// nothing committed, no revision, no identity. The refusal's own reason is deliberately dropped —
/// it is not expressible in a contract that predates it, and a producer that needs the distinction
/// reads the outcome from <see cref="BookMutations"/> directly.
/// </para>
/// <para>
/// This bridge is scaffolding with a known end. The final contraction ticket deletes it together
/// with the legacy façade, once no caller remains that cannot read an outcome.
/// </para>
/// </summary>
public static class LegacyBookCommandBridge
{
    /// <summary>The common shape: a command naming something the Book does not contain answers null.</summary>
    public static Task<BookCommandResult> ExecuteCommandAsync(
        this BookMutations mutations, BookMutation mutation, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(mutation, ct, BookMutationRejection.NotFound);

    /// <summary>
    /// The strict shape: every expected refusal stays a refusal, so an agent sees a 422 rather than a
    /// success-shaped null. <c>SetNarratorCharacter</c> is the one command written this way —
    /// <c>docs/agents/api.md</c> calls that out as what makes it unusual.
    /// </summary>
    public static Task<BookCommandResult> ExecuteCommandStrictAsync(
        this BookMutations mutations, BookMutation mutation, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(mutation, ct, answeredAsNull: []);

    public static async Task<BookCommandResult> ExecuteCommandAsync(
        this BookMutations mutations,
        BookMutation mutation,
        CancellationToken ct,
        params BookMutationRejection[] answeredAsNull) =>
        AsCommandResult(await mutations.CommitAsync(mutation, ct), answeredAsNull);

    /// <summary>
    /// The same translation for the handlers whose write goes through an artifact adapter rather
    /// than straight to <see cref="BookMutations"/>, and so already hold the outcome.
    /// </summary>
    public static BookCommandResult AsCommandResult(
        BookMutationOutcome outcome, params BookMutationRejection[] answeredAsNull) =>
        outcome switch
        {
            BookMutationOutcome.Committed committed => new(committed, committed.Receipt.Effects.CreatedId),
            BookMutationOutcome.Rejected rejected when answeredAsNull.Contains(rejected.Reason)
                => new(new BookMutationOutcome.NoChange(), null),
            _ => new(outcome, null),
        };

    /// <summary>
    /// The lossy step the legacy <see cref="IBookCommandHandler"/> façade still needs: one nullable
    /// id, with every refusal it did not soften raised as an exception. Only the callers that cannot
    /// yet read an outcome come through here, and ticket 15 deletes them with it.
    /// </summary>
    public static Guid? Flatten(BookCommandResult result, CancellationToken ct) =>
        result.Outcome switch
        {
            BookMutationOutcome.Committed or BookMutationOutcome.NoChange => result.EntityId,
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled
                => throw new OperationCanceledException(cancelled.Message, ct),
            BookMutationOutcome.Rejected rejected => throw new InvalidOperationException(rejected.Message),
            // Unreachable — the hierarchy is closed by a private constructor — but C# cannot prove it.
            _ => throw new NotSupportedException($"Unhandled mutation outcome {result.Outcome.GetType().Name}."),
        };
}
