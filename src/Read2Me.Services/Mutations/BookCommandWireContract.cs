using Read2Me.Services.Commands;

namespace Read2Me.Services.Mutations;

/// <summary>
/// Turns a mutation outcome into the answer <c>POST /api/projects/{folder}/commands</c> has always
/// given for that command, without losing the outcome itself.
/// <para>
/// The endpoint keeps its existing responses (ADR 0007), and which Refusal kinds a command has
/// always answered with <c>200 { "newEntityId": null }</c> differs per command: <c>missing</c>
/// nearly everywhere, <c>missing</c> and <c>invalid</c> for the Character roster and Voice Rule
/// commands, and none for a strict command. Each command's row in <see cref="BookCommandTable"/>
/// names its own, so what reaches <c>BookCommandApiAdapter</c> is a result it can map without
/// knowing any command's name.
/// </para>
/// <para>
/// A refusal a command has always answered as null is reported as
/// <see cref="BookMutationOutcome.NoChange"/>, because that is exactly what the wire says about it:
/// nothing committed, no revision, no identity. The refusal's own reason is deliberately dropped —
/// it is not expressible in a contract that predates it, and a producer that needs the distinction
/// reads the outcome from <see cref="BookMutations"/> directly.
/// </para>
/// <para>
/// The command layer is a permanent wire contract, and this is where a typed outcome is spoken in
/// its terms. Nothing above the command table uses it — every other producer, the Book View, the
/// Characters tab, the queues, imports and AI edits alike, commits through
/// <see cref="BookMutations"/> and reads the outcome, so the flattening reaches exactly the one
/// contract that predates it.
/// </para>
/// </summary>
public static class BookCommandWireContract
{
    /// <summary>Commits the mutation and answers it as the command does.</summary>
    public static async Task<BookCommandResult> ExecuteCommandAsync(
        this BookMutations mutations,
        BookMutation mutation,
        CancellationToken ct,
        params BookMutationRefusalKind[] answeredAsNull) =>
        AsCommandResult(await mutations.CommitAsync(mutation, ct), answeredAsNull);

    /// <summary>
    /// The same translation for the handlers whose write goes through an artifact adapter rather
    /// than straight to <see cref="BookMutations"/>, and so already hold the outcome.
    /// </summary>
    public static BookCommandResult AsCommandResult(
        BookMutationOutcome outcome, params BookMutationRefusalKind[] answeredAsNull) =>
        outcome switch
        {
            BookMutationOutcome.Committed committed => new(committed, committed.Receipt.Effects.CreatedId),
            BookMutationOutcome.Rejected rejected when answeredAsNull.Contains(rejected.Kind)
                => new(new BookMutationOutcome.NoChange(), null),
            _ => new(outcome, null),
        };
}
