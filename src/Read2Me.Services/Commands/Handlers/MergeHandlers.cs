using System;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;

namespace Read2Me.Services.Commands.Handlers;

public sealed class MergeVolumeHandler(ProjectDbSession session) : ICommandHandler<MergeVolumeCommand>
{
    public async Task<Guid?> HandleAsync(MergeVolumeCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanMergeVolume(c.VolumeId, c.Direction));
        return null;
    }
}

public sealed class MergePartHandler(ProjectDbSession session) : ICommandHandler<MergePartCommand>
{
    public async Task<Guid?> HandleAsync(MergePartCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanMergePart(c.PartId, c.Direction));
        return null;
    }
}

public sealed class MergeChapterHandler(ProjectDbSession session) : ICommandHandler<MergeChapterCommand>
{
    public async Task<Guid?> HandleAsync(MergeChapterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanMergeChapter(c.ChapterId, c.Direction));
        return null;
    }
}

public sealed class MergeParagraphHandler(ProjectDbSession session) : ICommandHandler<MergeParagraphCommand>
{
    public async Task<Guid?> HandleAsync(MergeParagraphCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanMergeParagraph(c.ParagraphId, c.Direction));
        return null;
    }
}

public sealed class MergeParagraphItemHandler(ProjectDbSession session) : ICommandHandler<MergeParagraphItemCommand>
{
    public async Task<Guid?> HandleAsync(MergeParagraphItemCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanMergeParagraphItem(c.ItemId, c.Direction));
        return null;
    }
}
