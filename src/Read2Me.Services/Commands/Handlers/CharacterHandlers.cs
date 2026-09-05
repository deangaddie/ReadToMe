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
/// The roster handlers pass <see cref="BookMutationRejection.Validation"/> to the legacy bridge as a
/// second answer-as-null case. The endpoint has always answered a protected-narrator rename, delete
/// or merge with <c>200 { "newEntityId": null }</c>; the mutations below now say <em>why</em> they
/// refused, and this is where that added precision is flattened back to the contract agents already
/// hold. <see cref="SetNarratorCharacterHandler"/> is the one that does not flatten.
/// </para>
/// </summary>
public sealed class SetItemCharacterHandler(BookMutations mutations) : ICommandHandler<SetItemCharacterCommand>
{
    public Task<Guid?> HandleAsync(SetItemCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new SetItemSpeakerMutation(c.FolderId, c.ItemId, c.CharacterId), ct);
}

/// <summary>
/// Creates a Character, or answers with the id of whoever already goes by that name — the command
/// is idempotent by name, which is what lets a discovery run be applied twice. Resolving the
/// existing id is a read, so it lives in <see cref="CharacterResolver"/> rather than in a mutation
/// that changed nothing.
/// </summary>
public sealed class CreateCharacterHandler(CharacterResolver resolver) : ICommandHandler<CreateCharacterCommand>
{
    public async Task<Guid?> HandleAsync(CreateCharacterCommand c, CancellationToken ct) =>
        await resolver.ResolveOrCreateAsync(c.FolderId, c.Name, ct);
}

public sealed class SetParagraphCharacterHandler(BookMutations mutations)
    : ICommandHandler<SetParagraphCharacterCommand>
{
    public Task<Guid?> HandleAsync(SetParagraphCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new SetParagraphSpeakerMutation(c.FolderId, c.ParagraphId, c.CharacterId, c.VoiceInstructions), ct);
}

public sealed class SetParagraphsCharacterHandler(BookMutations mutations)
    : ICommandHandler<SetParagraphsCharacterCommand>
{
    public Task<Guid?> HandleAsync(SetParagraphsCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new SetParagraphsSpeakerMutation(c.FolderId, c.ParagraphIds, c.CharacterId), ct);
}

public sealed class AddCharacterAliasHandler(BookMutations mutations) : ICommandHandler<AddCharacterAliasCommand>
{
    public Task<Guid?> HandleAsync(AddCharacterAliasCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new AddCharacterAliasMutation(c.FolderId, c.CharacterId, c.Name), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class RemoveCharacterAliasHandler(BookMutations mutations) : ICommandHandler<RemoveCharacterAliasCommand>
{
    public Task<Guid?> HandleAsync(RemoveCharacterAliasCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new RemoveCharacterAliasMutation(c.FolderId, c.AliasId), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class MergeCharactersHandler(BookMutations mutations) : ICommandHandler<MergeCharactersCommand>
{
    public Task<Guid?> HandleAsync(MergeCharactersCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new MergeCharactersMutation(c.FolderId, c.SurvivorId, c.MergedId, c.AddNameAsAlias), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class RenameCharacterHandler(BookMutations mutations) : ICommandHandler<RenameCharacterCommand>
{
    public Task<Guid?> HandleAsync(RenameCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new RenameCharacterMutation(c.FolderId, c.CharacterId, c.Name), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class DeleteCharacterHandler(BookMutations mutations) : ICommandHandler<DeleteCharacterCommand>
{
    public Task<Guid?> HandleAsync(DeleteCharacterCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new DeleteCharacterMutation(c.FolderId, c.CharacterId), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}
