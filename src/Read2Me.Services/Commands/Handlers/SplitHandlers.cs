using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The four splits, migrated to <see cref="BookMutations"/> (ADR 0007). Each handler is now only a
/// translation: the write, its transaction, its commit point and the split relationship a Book View
/// keeps its place by all live in the mutation implementations. The handlers stay registered so
/// <c>POST /api/projects/{folder}/commands</c> keeps its <c>newEntityId</c> response unchanged.
/// </summary>
public sealed class SplitAtPartHandler(BookMutations mutations) : ICommandHandler<SplitAtPartCommand>
{
    public Task<BookCommandResult> HandleAsync(SplitAtPartCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SplitAtPartMutation(c.FolderId, c.PartId, c.NewVolumeTitle), ct);
}

public sealed class SplitAtChapterHandler(BookMutations mutations) : ICommandHandler<SplitAtChapterCommand>
{
    public Task<BookCommandResult> HandleAsync(SplitAtChapterCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SplitAtChapterMutation(c.FolderId, c.ChapterId, c.NewPartTitle), ct);
}

public sealed class SplitAtParagraphHandler(BookMutations mutations) : ICommandHandler<SplitAtParagraphCommand>
{
    public Task<BookCommandResult> HandleAsync(SplitAtParagraphCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SplitAtParagraphMutation(c.FolderId, c.ParagraphId, c.NewChapterTitle), ct);
}

public sealed class SplitAtItemHandler(BookMutations mutations) : ICommandHandler<SplitAtItemCommand>
{
    public Task<BookCommandResult> HandleAsync(SplitAtItemCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new SplitAtItemMutation(c.FolderId, c.ItemId), ct);
}
