using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The narrator link's command shape — the agent API and the book banner both land here, so human
/// and machine paths cannot drift (ADR-0004). The write itself is
/// <see cref="SetNarratorCharacterMutation"/>, so every open Book View reconciles its narrator
/// identity from the receipt (ADR 0007).
/// </summary>
/// <remarks>
/// Deliberately breaks the sibling handlers' answer-as-null house style: every expected refusal
/// throws, and <c>CommandEndpoints</c> turns that into a 422. Answering null would render rejection
/// to an agent as <c>200 { "newEntityId": null }</c>, indistinguishable from success — including for
/// a link to a character this project does not have, which is why <c>NotFound</c> is not flattened
/// here either.
/// </remarks>
public sealed class SetNarratorCharacterHandler(BookMutations mutations)
    : ICommandHandler<SetNarratorCharacterCommand>
{
    public Task<BookCommandResult> HandleAsync(SetNarratorCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandStrictAsync(new SetNarratorCharacterMutation(c.FolderId, c.CharacterId), ct);
}
