using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Turns a mutation outcome into the answer <c>POST /api/projects/{folder}/commands</c> has always
/// given for that command, without losing the outcome itself.
/// <para>
/// The endpoint keeps its existing responses (ADR 0007), and which refusals a command has always
/// answered with <c>200 { "newEntityId": null }</c> differs per command: a
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
/// This is not scaffolding any more, which is why it is no longer named as such: the command layer
/// is a permanent wire contract, and this is where a typed outcome is spoken in its terms. Nothing
/// above the command handlers uses it — every other producer, the Book View, the Characters tab,
/// the queues, imports and AI edits alike, commits through <see cref="BookMutations"/> and reads
/// the outcome, so the flattening reaches exactly the one contract that predates it.
/// </para>
/// </summary>
public static class BookCommandWireContract
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
}
