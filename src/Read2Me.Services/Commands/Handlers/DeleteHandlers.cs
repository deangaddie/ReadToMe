using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The five deletions, migrated to <see cref="BookMutations"/> (ADR 0007). Each handler is a
/// translation only.
/// <para>
/// The endpoint's contract is unchanged, including the shape these had before: deleting a node the
/// Book does not contain answers with no id rather than failing, which
/// <c>ExecuteCommandAsync</c> preserves by mapping the mutation's <c>NotFound</c> to null.
/// </para>
/// </summary>
public sealed class DeleteVolumeHandler(BookMutations mutations) : ICommandHandler<DeleteVolumeCommand>
{
    public Task<BookCommandResult> HandleAsync(DeleteVolumeCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new DeleteVolumeMutation(c.FolderId, c.VolumeId), ct);
}

public sealed class DeletePartHandler(BookMutations mutations) : ICommandHandler<DeletePartCommand>
{
    public Task<BookCommandResult> HandleAsync(DeletePartCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new DeletePartMutation(c.FolderId, c.PartId), ct);
}

public sealed class DeleteChapterHandler(BookMutations mutations) : ICommandHandler<DeleteChapterCommand>
{
    public Task<BookCommandResult> HandleAsync(DeleteChapterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new DeleteChapterMutation(c.FolderId, c.ChapterId), ct);
}

public sealed class DeleteParagraphHandler(BookMutations mutations) : ICommandHandler<DeleteParagraphCommand>
{
    public Task<BookCommandResult> HandleAsync(DeleteParagraphCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new DeleteParagraphMutation(c.FolderId, c.ParagraphId), ct);
}

public sealed class DeleteParagraphItemHandler(BookMutations mutations)
    : ICommandHandler<DeleteParagraphItemCommand>
{
    public Task<BookCommandResult> HandleAsync(DeleteParagraphItemCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new DeleteParagraphItemMutation(c.FolderId, c.ItemId), ct);
}
