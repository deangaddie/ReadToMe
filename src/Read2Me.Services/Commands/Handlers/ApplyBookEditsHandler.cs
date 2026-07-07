using Read2Me.Core.Models;

namespace Read2Me.Services.Commands.Handlers;

public sealed class ApplyBookEditsHandler(ProjectDbSession session) : ICommandHandler<ApplyBookEditsCommand>
{
    public async Task<Guid?> HandleAsync(ApplyBookEditsCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        foreach (var edit in c.Edits)
        {
            switch (edit.Kind)
            {
                case BookEditTargetKind.VolumeTitle:
                    var volume = await db.Volumes.FindAsync([edit.Id], ct);
                    if (volume != null) volume.Title = edit.NewValue;
                    break;
                case BookEditTargetKind.PartTitle:
                    var part = await db.Parts.FindAsync([edit.Id], ct);
                    if (part != null) part.Title = edit.NewValue;
                    break;
                case BookEditTargetKind.ChapterTitle:
                    var chapter = await db.Chapters.FindAsync([edit.Id], ct);
                    if (chapter != null) chapter.Title = edit.NewValue;
                    break;
                case BookEditTargetKind.ParagraphItemText:
                    var item = await db.ParagraphItems.FindAsync([edit.Id], ct);
                    if (item != null) item.Text = edit.NewValue;
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        return null;
    }
}
