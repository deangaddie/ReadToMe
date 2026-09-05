using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands;

/// <summary>
/// What a <see cref="Read2Me.Core.Models.BookCommand"/> did, in the two terms
/// <c>POST /api/projects/{folder}/commands</c> answers in: the mutation outcome, and the identity
/// the endpoint reports as <c>newEntityId</c>.
/// <para>
/// The identity is carried separately rather than read off the receipt because the wire contract and
/// the receipt disagree in both directions. <c>CreateCharacter</c> answers with the id of whoever
/// already goes by that name, which is a read and not a created identity; <c>InsertPauseParagraph</c>
/// creates a Paragraph the receipt names and the command has never reported. ADR 0007 keeps the
/// endpoint's JSON fixed through the migration, so those two quirks live with the handler that owns
/// them and the adapter stays a plain outcome map.
/// </para>
/// </summary>
public sealed record BookCommandResult(BookMutationOutcome Outcome, Guid? EntityId)
{
    /// <summary>For the one command that creates something it has never reported creating.</summary>
    public BookCommandResult WithoutIdentity() => this with { EntityId = null };
}
