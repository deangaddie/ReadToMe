using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;

namespace Read2Me.Services.Commands.Handlers;

internal sealed class AddBookTitleHandler(ProjectDbSession session) : ICommandHandler<AddBookTitleCommand>
{
    public async Task<Guid?> HandleAsync(AddBookTitleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var project = await db.Projects.SingleOrDefaultAsync(ct);
        if (project == null) return null;
        var h = await BookMutationApplier.LoadBookHierarchyAsync(db);
        var plan = h.PlanFrontMatterInsert();
        if (plan == null) return null;
        var (mutation, chapterId, _) = plan.Value;
        await BookMutationApplier.ApplyMutationAsync(db, mutation);
        var titlePara = TitleInserter.AddTitleParagraph(db, chapterId, project.BookTitle, null);
        TitleInserter.AddTitleParagraphAfter(db, chapterId, $"By {project.Author}", titlePara.Order);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class AddVolumeTitlesHandler(ProjectDbSession session) : ICommandHandler<AddVolumeTitlesCommand>
{
    public async Task<Guid?> HandleAsync(AddVolumeTitlesCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var h = await BookMutationApplier.LoadBookHierarchyAsync(db);
        foreach (var (_, title, newChapter, _) in h.PlanVolumeTitleChapters())
        {
            db.Chapters.Add(newChapter);
            TitleInserter.AddTitleParagraph(db, newChapter.Id, title, null);
        }
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class AddPartTitlesHandler(ProjectDbSession session) : ICommandHandler<AddPartTitlesCommand>
{
    public async Task<Guid?> HandleAsync(AddPartTitlesCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var h = await BookMutationApplier.LoadBookHierarchyAsync(db);
        foreach (var (_, title, newChapter, _) in h.PlanPartTitleChapters())
        {
            db.Chapters.Add(newChapter);
            TitleInserter.AddTitleParagraph(db, newChapter.Id, title, null);
        }
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class AddChapterTitlesHandler(ProjectDbSession session) : ICommandHandler<AddChapterTitlesCommand>
{
    public async Task<Guid?> HandleAsync(AddChapterTitlesCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var h = await BookMutationApplier.LoadBookHierarchyAsync(db);
        foreach (var (chapterId, title, firstParagraphOrder) in h.PlanChapterTitleInsertions())
            TitleInserter.AddTitleParagraph(db, chapterId, title, firstParagraphOrder);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class AddPausesHandler(ProjectDbSession session) : ICommandHandler<AddPausesCommand>
{
    public async Task<Guid?> HandleAsync(AddPausesCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var h = await BookMutationApplier.LoadBookHierarchyAsync(db);
        foreach (var p in h.PlanPauseInsertions())
            PauseInserter.AddPauseParagraph(db, p.ChapterId, p.PauseType, p.AfterOrder, p.BeforeOrder);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class InsertPauseParagraphHandler(ProjectDbSession session) : ICommandHandler<InsertPauseParagraphCommand>
{
    private static ParagraphItemType MapPauseKind(PauseKind kind) => kind switch
    {
        PauseKind.Pause          => ParagraphItemType.Pause,
        PauseKind.ParagraphPause => ParagraphItemType.ParagraphPause,
        PauseKind.ChapterPause   => ParagraphItemType.ChapterPause,
        PauseKind.PartPause      => ParagraphItemType.PartPause,
        PauseKind.VolumePause    => ParagraphItemType.VolumePause,
        _                        => ParagraphItemType.Pause,
    };

    public async Task<Guid?> HandleAsync(InsertPauseParagraphCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var item = await db.ParagraphItems.FindAsync(c.AnchorItemId);
        if (item == null) return null;
        var paragraph = await db.Paragraphs
            .Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == item.ParagraphId, ct);
        if (paragraph == null) return null;

        var siblings = await db.Paragraphs
            .Where(p => p.ChapterId == paragraph.ChapterId)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);

        var idx = siblings.FindIndex(p => p.Id == paragraph.Id);
        if (idx < 0) return null;

        string? afterOrder, beforeOrder;
        if (c.Position == PauseInsertPosition.Before)
        {
            afterOrder  = idx > 0 ? siblings[idx - 1].Order : null;
            beforeOrder = paragraph.Order;
        }
        else
        {
            afterOrder  = paragraph.Order;
            beforeOrder = idx < siblings.Count - 1 ? siblings[idx + 1].Order : null;
        }

        PauseInserter.AddPauseParagraph(db, paragraph.ChapterId, MapPauseKind(c.PauseKind), afterOrder, beforeOrder);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

internal sealed class ClearBookContentHandler(ProjectDbSession session) : ICommandHandler<ClearBookContentCommand>
{
    public async Task<Guid?> HandleAsync(ClearBookContentCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ParagraphItems.ExecuteDeleteAsync(ct);
        await db.Paragraphs.ExecuteDeleteAsync(ct);
        await db.Chapters.ExecuteDeleteAsync(ct);
        await db.Parts.ExecuteDeleteAsync(ct);
        await db.Volumes.ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return null;
    }
}
