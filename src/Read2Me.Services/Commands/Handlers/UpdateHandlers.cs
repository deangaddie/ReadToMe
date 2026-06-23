using Read2Me.Core.Models;

namespace Read2Me.Services.Commands.Handlers;

public sealed class UpdateVolumeTitleHandler(ProjectDbSession session) : ICommandHandler<UpdateVolumeTitleCommand>
{
    public async Task<Guid?> HandleAsync(UpdateVolumeTitleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Volumes.FindAsync(c.VolumeId);
        if (e == null) return null;
        e.Title = c.Title;
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class UpdatePartTitleHandler(ProjectDbSession session) : ICommandHandler<UpdatePartTitleCommand>
{
    public async Task<Guid?> HandleAsync(UpdatePartTitleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Parts.FindAsync(c.PartId);
        if (e == null) return null;
        e.Title = c.Title;
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class UpdateChapterTitleHandler(ProjectDbSession session) : ICommandHandler<UpdateChapterTitleCommand>
{
    public async Task<Guid?> HandleAsync(UpdateChapterTitleCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.Chapters.FindAsync(c.ChapterId);
        if (e == null) return null;
        e.Title = c.Title;
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class UpdateParagraphItemTextHandler(ProjectDbSession session) : ICommandHandler<UpdateParagraphItemTextCommand>
{
    public async Task<Guid?> HandleAsync(UpdateParagraphItemTextCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.ParagraphItems.FindAsync(c.ItemId);
        if (e == null) return null;
        e.Text = c.Text;
        await db.SaveChangesAsync();
        return null;
    }
}

public sealed class SetParagraphItemAudioHandler(ProjectDbSession session) : ICommandHandler<SetParagraphItemAudioCommand>
{
    public async Task<Guid?> HandleAsync(SetParagraphItemAudioCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.ParagraphItems.FindAsync(c.ItemId);
        if (e == null) return null;
        e.AudioFileName = c.AudioFileName;
        await db.SaveChangesAsync();
        return null;
    }
}
