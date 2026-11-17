using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Creates a Character, or answers with the id of whoever already goes by that name — the command
/// is idempotent by name, which is what lets a discovery run be applied twice. Resolving the
/// existing id is a read, so it lives in <see cref="CharacterResolver"/> rather than in a mutation
/// that changed nothing.
/// </summary>
public sealed class CreateCharacterHandler(CharacterResolver resolver) : ICommandHandler<CreateCharacterCommand>
{
    public async Task<BookCommandResult> HandleAsync(CreateCharacterCommand c, CancellationToken ct)
    {
        // The identity is answered either way — that is what makes the command idempotent by name —
        // but the outcome still says which of the two happened.
        var (outcome, id) = await resolver.ResolveOrCreateWithOutcomeAsync(c.FolderId, c.Name, ct);
        return new BookCommandResult(outcome, id);
    }
}

/// <summary>
/// "New character…" plus its assign as one request: the resolve-or-create of
/// <see cref="CreateCharacterHandler"/>, then the speaker mutation for the one target named. Strict
/// where its single-step siblings are not: a malformed request is refused before anything is written,
/// and an unknown target is a refusal rather than a null, because by then the character exists and
/// the answer must say the assign did not happen.
/// </summary>
public sealed class CreateCharacterAndAssignHandler(CharacterResolver resolver, BookMutations mutations)
    : ICommandHandler<CreateCharacterAndAssignCommand>
{
    public async Task<BookCommandResult> HandleAsync(CreateCharacterAndAssignCommand c, CancellationToken ct)
    {
        var name = c.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Refused("A new character needs a name.");

        var targets = (c.ItemId is null ? 0 : 1) + (c.ParagraphId is null ? 0 : 1) + (c.ParagraphIds is null ? 0 : 1);
        if (targets != 1)
            return Refused("Name exactly one of itemId, paragraphId or paragraphIds to assign the character to.");
        if (c.ParagraphIds is { Count: 0 })
            return Refused("paragraphIds names no paragraph.");

        var (created, id) = await resolver.ResolveOrCreateWithOutcomeAsync(c.FolderId, name, ct);
        if (id is not { } characterId) return new BookCommandResult(created, null);

        BookMutation assign = c switch
        {
            { ItemId: { } item } => new SetItemSpeakerMutation(c.FolderId, item, characterId),
            { ParagraphId: { } paragraph } => new SetParagraphSpeakerMutation(c.FolderId, paragraph, characterId),
            _ => new SetParagraphsSpeakerMutation(c.FolderId, c.ParagraphIds!, characterId),
        };
        var assigned = await mutations.CommitAsync(assign, ct);

        // Either write committing is a change; an assign that found the speaker already stamped
        // leaves whatever the resolve did.
        return assigned switch
        {
            BookMutationOutcome.Rejected => new BookCommandResult(assigned, null),
            BookMutationOutcome.Committed => new BookCommandResult(assigned, characterId),
            _ => new BookCommandResult(created, characterId),
        };
    }

    private static BookCommandResult Refused(string message) =>
        new(new BookMutationOutcome.Rejected(BookMutationRejection.Validation, message), null);
}
