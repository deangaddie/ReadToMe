using System;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Commands.Handlers;

public sealed class SplitAtPartHandler(ProjectDbSession session) : ICommandHandler<SplitAtPartCommand>
{
    public async Task<Guid?> HandleAsync(SplitAtPartCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var mutation = await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanSplitVolume(c.PartId, c.NewVolumeTitle));
        return mutation != null ? ((Volume)mutation.ToAdd[0]).Id : null;
    }
}

public sealed class SplitAtChapterHandler(ProjectDbSession session) : ICommandHandler<SplitAtChapterCommand>
{
    public async Task<Guid?> HandleAsync(SplitAtChapterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var mutation = await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanSplitPart(c.ChapterId, c.NewPartTitle));
        return mutation != null ? ((Part)mutation.ToAdd[0]).Id : null;
    }
}

public sealed class SplitAtParagraphHandler(ProjectDbSession session) : ICommandHandler<SplitAtParagraphCommand>
{
    public async Task<Guid?> HandleAsync(SplitAtParagraphCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var mutation = await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanSplitChapter(c.ParagraphId, c.NewChapterTitle));
        return mutation != null ? ((Chapter)mutation.ToAdd[0]).Id : null;
    }
}

public sealed class SplitAtItemHandler(ProjectDbSession session) : ICommandHandler<SplitAtItemCommand>
{
    public async Task<Guid?> HandleAsync(SplitAtItemCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var mutation = await BookMutationApplier.PlanAndApplyAsync(db, h => h.PlanSplitParagraph(c.ItemId));
        return mutation != null ? ((Paragraph)mutation.ToAdd[0]).Id : null;
    }
}
