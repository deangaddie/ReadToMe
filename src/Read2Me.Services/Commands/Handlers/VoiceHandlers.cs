using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The Voice and Voice Rule commands, migrated to <see cref="BookMutations"/> (ADR 0007). Each
/// handler is now only a translation: the writes, their transaction, and the
/// effects a Book View reconciles from all live behind the mutation seam. They stay
/// registered so <c>POST /api/projects/{folder}/commands</c> keeps its existing request and response
/// shape.
/// <para>
/// Every one of these has always answered a gesture aimed at a Voice, rule or Character the project
/// does not contain with <c>200 { "newEntityId": null }</c>, so <see cref="BookMutationRejection.NotFound"/>
/// stays flattened. The three that gained a <see cref="BookMutationRejection.Validation"/> refusal —
/// a rule pointed at another Character's Voice, and deleting or reordering the default rule — name it
/// too, because those were silent no-ops before this migration and the endpoint's contract is not
/// this slice's to change.
/// </para>
/// </summary>
public sealed class CreateVoiceHandler(BookMutations mutations) : ICommandHandler<CreateVoiceCommand>
{
    public Task<BookCommandResult> HandleAsync(CreateVoiceCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new CreateVoiceMutation(c.FolderId, c.CharacterId, c.Name, c.IsGenerated), ct);
}

public sealed class SetVoiceDefaultHandler(BookMutations mutations) : ICommandHandler<SetVoiceDefaultCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceDefaultCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetVoiceDefaultMutation(c.FolderId, c.VoiceId), ct);
}

public sealed class UpdateVoiceHandler(BookMutations mutations) : ICommandHandler<UpdateVoiceCommand>
{
    public Task<BookCommandResult> HandleAsync(UpdateVoiceCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new UpdateVoiceMutation(c.FolderId, c.VoiceId, c.Name, c.Description), ct);
}

public sealed class SetVoiceDesignPromptHandler(BookMutations mutations)
    : ICommandHandler<SetVoiceDesignPromptCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceDesignPromptCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetVoiceDesignPromptMutation(c.FolderId, c.VoiceId, c.Prompt), ct);
}

public sealed class SetVoiceSettingsOverrideHandler(BookMutations mutations)
    : ICommandHandler<SetVoiceSettingsOverrideCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceSettingsOverrideCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetVoiceDesignSettingsOverrideMutation(c.FolderId, c.VoiceId, c.Json), ct);
}

public sealed class SetVoiceTtsSettingsOverrideHandler(BookMutations mutations)
    : ICommandHandler<SetVoiceTtsSettingsOverrideCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceTtsSettingsOverrideCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetVoiceTtsSettingsOverrideMutation(c.FolderId, c.VoiceId, c.Json), ct);
}

public sealed class SetVoiceTranscriptHandler(BookMutations mutations) : ICommandHandler<SetVoiceTranscriptCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceTranscriptCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetVoiceTranscriptMutation(c.FolderId, c.VoiceId, c.Transcript), ct);
}

public sealed class SetVoiceAudioHandler(BookMutations mutations) : ICommandHandler<SetVoiceAudioCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceAudioCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SetVoiceAudioMutation(c.FolderId, c.VoiceId, c.AudioFileName), ct);
}

public sealed class SetVoiceGeneratedHandler(BookMutations mutations) : ICommandHandler<SetVoiceGeneratedCommand>
{
    public Task<BookCommandResult> HandleAsync(SetVoiceGeneratedCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new SetVoiceGeneratedMutation(c.FolderId, c.VoiceId, c.AudioFileName, c.Transcript, c.DesignPrompt), ct);
}

/// <summary>
/// The two gestures that take a Voice's recording away. Both cross <see cref="BookMutations"/>
/// through <see cref="IVoiceAudioRemover"/> rather than directly, because the file has to go
/// <em>after</em> the commit that stops naming it (ADR 0007) — and the outcome is flattened here to
/// the same null the endpoint has always answered.
/// </summary>
public sealed class SetVoiceSourceHandler(IVoiceAudioRemover voiceAudio) : ICommandHandler<SetVoiceSourceCommand>
{
    public async Task<BookCommandResult> HandleAsync(SetVoiceSourceCommand c, CancellationToken ct) =>
        LegacyBookCommandBridge.AsCommandResult(
            await voiceAudio.SetVoiceSourceAsync(c.FolderId, c.VoiceId, c.IsGenerated, ct),
            BookMutationRejection.NotFound);
}

public sealed class DeleteVoiceHandler(IVoiceAudioRemover voiceAudio) : ICommandHandler<DeleteVoiceCommand>
{
    public async Task<BookCommandResult> HandleAsync(DeleteVoiceCommand c, CancellationToken ct) =>
        LegacyBookCommandBridge.AsCommandResult(
            await voiceAudio.DeleteVoiceAsync(c.FolderId, c.VoiceId, ct),
            BookMutationRejection.NotFound);
}

public sealed class CreateVoiceRuleHandler(BookMutations mutations) : ICommandHandler<CreateVoiceRuleCommand>
{
    public Task<BookCommandResult> HandleAsync(CreateVoiceRuleCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new CreateVoiceRuleMutation(
                c.FolderId, c.CharacterId, c.VoiceId, c.FromLevel, c.FromNodeId, c.ToLevel, c.ToNodeId), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class DeleteVoiceRuleHandler(BookMutations mutations) : ICommandHandler<DeleteVoiceRuleCommand>
{
    public Task<BookCommandResult> HandleAsync(DeleteVoiceRuleCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new DeleteVoiceRuleMutation(c.FolderId, c.RuleId), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}

public sealed class MoveVoiceRuleHandler(BookMutations mutations) : ICommandHandler<MoveVoiceRuleCommand>
{
    public Task<BookCommandResult> HandleAsync(MoveVoiceRuleCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new MoveVoiceRuleMutation(c.FolderId, c.RuleId, c.Direction), ct,
            BookMutationRejection.NotFound, BookMutationRejection.Validation);
}
