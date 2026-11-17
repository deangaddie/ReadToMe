using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The two gestures that take a Voice's recording away. Both cross <see cref="BookMutations"/>
/// through <see cref="IVoiceAudioRemover"/> rather than directly, because the file has to go
/// <em>after</em> the commit that stops naming it (ADR 0007) — and the outcome is flattened here to
/// the same null the endpoint has always answered.
/// </summary>
public sealed class SetVoiceSourceHandler(IVoiceAudioRemover voiceAudio) : ICommandHandler<SetVoiceSourceCommand>
{
    public async Task<BookCommandResult> HandleAsync(SetVoiceSourceCommand c, CancellationToken ct) =>
        BookCommandWireContract.AsCommandResult(
            await voiceAudio.SetVoiceSourceAsync(c.FolderId, c.VoiceId, c.IsGenerated, ct),
            BookMutationRefusalKind.Missing);
}

public sealed class DeleteVoiceHandler(IVoiceAudioRemover voiceAudio) : ICommandHandler<DeleteVoiceCommand>
{
    public async Task<BookCommandResult> HandleAsync(DeleteVoiceCommand c, CancellationToken ct) =>
        BookCommandWireContract.AsCommandResult(
            await voiceAudio.DeleteVoiceAsync(c.FolderId, c.VoiceId, ct),
            BookMutationRefusalKind.Missing);
}
