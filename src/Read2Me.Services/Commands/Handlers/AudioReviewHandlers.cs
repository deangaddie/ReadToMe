using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The review commands, migrated to <see cref="BookMutations"/> (ADR 0007). The row-presence rules
/// live in <see cref="Mutations.Implementations.AudioEffects"/>; these handlers stay registered so
/// <c>POST /api/projects/{folder}/commands</c> keeps its existing request and response shape.
/// <para>
/// Both still answer <c>null</c>: a review that changed nothing and an item the Book does not
/// contain flatten to it, as they did when the handlers owned the save.
/// </para>
/// </summary>
public sealed class SetAudioReviewHandler(BookMutations mutations) : ICommandHandler<SetAudioReviewCommand>
{
    public Task<BookCommandResult> HandleAsync(SetAudioReviewCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(
            new SetAudioReviewMutation(c.FolderId, c.ParagraphItemId, new AudioReviewVerdict(
                c.NormalizeOk, c.NormalizeReason,
                c.VerifyOk, c.Wer, c.VerifyReason,
                c.Transcript, c.OriginalTextSnapshot)), ct);
}

public sealed class DismissAudioReviewHandler(BookMutations mutations) : ICommandHandler<DismissAudioReviewCommand>
{
    public Task<BookCommandResult> HandleAsync(DismissAudioReviewCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new DismissAudioReviewMutation(c.FolderId, c.ParagraphItemId), ct);
}
