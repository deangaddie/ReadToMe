using System;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Commands.Handlers;

public sealed class DeleteVolumeHandler(ProjectDbSession session) : ICommandHandler<DeleteVolumeCommand>
{
    public async Task<Guid?> HandleAsync(DeleteVolumeCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Volumes.FindAsync(c.VolumeId);
        if (e == null) return null;
        db.Volumes.Remove(e);
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class DeletePartHandler(ProjectDbSession session) : ICommandHandler<DeletePartCommand>
{
    public async Task<Guid?> HandleAsync(DeletePartCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Parts.FindAsync(c.PartId);
        if (e == null) return null;
        db.Parts.Remove(e);
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class DeleteChapterHandler(ProjectDbSession session) : ICommandHandler<DeleteChapterCommand>
{
    public async Task<Guid?> HandleAsync(DeleteChapterCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Chapters.FindAsync(c.ChapterId);
        if (e == null) return null;
        db.Chapters.Remove(e);
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class DeleteParagraphHandler(ProjectDbSession session) : ICommandHandler<DeleteParagraphCommand>
{
    public async Task<Guid?> HandleAsync(DeleteParagraphCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Paragraphs.FindAsync(c.ParagraphId);
        if (e == null) return null;
        db.Paragraphs.Remove(e);
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class DeleteParagraphItemHandler(ProjectDbSession session) : ICommandHandler<DeleteParagraphItemCommand>
{
    public async Task<Guid?> HandleAsync(DeleteParagraphItemCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.ParagraphItems.FindAsync(c.ItemId);
        if (e == null) return null;
        db.ParagraphItems.Remove(e);
        await db.SaveChangesAsync();
        return null;
    }
}
