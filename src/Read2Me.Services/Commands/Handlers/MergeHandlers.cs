using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The five merges, migrated to <see cref="BookMutations"/> (ADR 0007). Each handler is a
/// translation only: the merge itself lives in the mutation implementation, where the survivor it
/// folded into is reported so an open Book View can move expansion onto it.
/// <para>
/// The endpoint's contract is unchanged. A merge never created anything, so it still answers with
/// no id, and a first or last sibling with nothing to merge into is still a quiet no-op.
/// </para>
/// </summary>
public sealed class MergeVolumeHandler(BookMutations mutations) : ICommandHandler<MergeVolumeCommand>
{
    public Task<Guid?> HandleAsync(MergeVolumeCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new MergeVolumeMutation(c.FolderId, c.VolumeId, c.Direction), ct);
}

public sealed class MergePartHandler(BookMutations mutations) : ICommandHandler<MergePartCommand>
{
    public Task<Guid?> HandleAsync(MergePartCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new MergePartMutation(c.FolderId, c.PartId, c.Direction), ct);
}

public sealed class MergeChapterHandler(BookMutations mutations) : ICommandHandler<MergeChapterCommand>
{
    public Task<Guid?> HandleAsync(MergeChapterCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new MergeChapterMutation(c.FolderId, c.ChapterId, c.Direction), ct);
}

public sealed class MergeParagraphHandler(BookMutations mutations) : ICommandHandler<MergeParagraphCommand>
{
    public Task<Guid?> HandleAsync(MergeParagraphCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new MergeParagraphMutation(c.FolderId, c.ParagraphId, c.Direction), ct);
}

public sealed class MergeParagraphItemHandler(BookMutations mutations)
    : ICommandHandler<MergeParagraphItemCommand>
{
    public Task<Guid?> HandleAsync(MergeParagraphItemCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new MergeParagraphItemMutation(c.FolderId, c.ItemId, c.Direction), ct);
}
