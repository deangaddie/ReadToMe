using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The speaker and Character-roster commands, migrated to <see cref="BookMutations"/> (ADR 0007).
/// Each handler is now only a translation: the writes, their transaction, and the effects a Book
/// View reconciles from all live in the mutation implementations. They stay registered so
/// <c>POST /api/projects/{folder}/commands</c> keeps its existing request and response shape.
/// <para>
/// The commands keep saying "character" and the speaker mutations say "speaker" because the wire
/// names are fixed and the domain's word is not: narration is a speaker too (ADR-0006), so a
/// gesture that can stamp the narrator is not a "set character". The two vocabularies meet here, in
/// one line each, until the wire contract is free to move.
/// </para>
/// <para>
/// The three handlers whose mutation can refuse a protected-narrator gesture — rename, merge and
/// delete — pass <see cref="BookMutationRejection.Validation"/> to the legacy bridge as a second
/// answer-as-null case. The endpoint has always answered those with <c>200 { "newEntityId": null }</c>;
/// the mutations below now say <em>why</em> they refused, and this is where that added precision is
/// flattened back to the contract agents already hold.
/// <see cref="SetNarratorCharacterHandler"/> is the one that does not flatten.
/// </para>
/// </summary>
public sealed class SetItemCharacterHandler(BookMutations mutations) : ICommandHandler<SetItemCharacterCommand>
{
    public Task<BookCommandResult> HandleAsync(SetItemCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetItemSpeakerMutation(c.FolderId, c.ItemId, c.CharacterId), ct);
}

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

public sealed class SetParagraphCharacterHandler(BookMutations mutations)
    : ICommandHandler<SetParagraphCharacterCommand>
{
    public Task<BookCommandResult> HandleAsync(SetParagraphCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new SetParagraphSpeakerMutation(c.FolderId, c.ParagraphId, c.CharacterId, c.VoiceInstructions), ct);
}

public sealed class SetParagraphsCharacterHandler(BookMutations mutations)
    : ICommandHandler<SetParagraphsCharacterCommand>
{
    public Task<BookCommandResult> HandleAsync(SetParagraphsCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new SetParagraphsSpeakerMutation(c.FolderId, c.ParagraphIds, c.CharacterId), ct);
}

public sealed class ReleaseNarrationHandler(BookMutations mutations)
    : ICommandHandler<ReleaseNarrationCommand>
{
    public Task<BookCommandResult> HandleAsync(ReleaseNarrationCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new ReleaseNarrationMutation(c.FolderId, c.Scope, c.NodeId), ct);
}

public sealed class AddCharacterAliasHandler(BookMutations mutations) : ICommandHandler<AddCharacterAliasCommand>
{
    public Task<BookCommandResult> HandleAsync(AddCharacterAliasCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new AddCharacterAliasMutation(c.FolderId, c.CharacterId, c.Name), ct);
}

public sealed class RemoveCharacterAliasHandler(BookMutations mutations) : ICommandHandler<RemoveCharacterAliasCommand>
{
    public Task<BookCommandResult> HandleAsync(RemoveCharacterAliasCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new RemoveCharacterAliasMutation(c.FolderId, c.AliasId), ct);
}

public sealed class MergeCharactersHandler(BookMutations mutations) : ICommandHandler<MergeCharactersCommand>
{
    public Task<BookCommandResult> HandleAsync(MergeCharactersCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new MergeCharactersMutation(c.FolderId, c.SurvivorId, c.MergedId, c.AddNameAsAlias), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class RenameCharacterHandler(BookMutations mutations) : ICommandHandler<RenameCharacterCommand>
{
    public Task<BookCommandResult> HandleAsync(RenameCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new RenameCharacterMutation(c.FolderId, c.CharacterId, c.Name), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class DeleteCharacterHandler(BookMutations mutations) : ICommandHandler<DeleteCharacterCommand>
{
    public Task<BookCommandResult> HandleAsync(DeleteCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new DeleteCharacterMutation(c.FolderId, c.CharacterId), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}
