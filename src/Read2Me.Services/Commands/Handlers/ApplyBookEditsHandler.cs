using Read2Me.Core.Models;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Applies an approved AI edit program. Titles are set directly; item text goes through
/// <see cref="ParagraphItemTextEdit"/> rather than a bare assignment, because this handler reaches
/// item text by its own path and would otherwise leave the stale-audio hole open on the AI route
/// while the item menu had it closed.
/// </summary>
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
                    // A rewritten item's WAV speaks words it no longer has, so the edit discards
                    // the audio and any verdict on it — same rule as the item menu.
                    if (item != null) await ParagraphItemTextEdit.ApplyAsync(db, item, edit.NewValue, ct);
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        return null;
    }
}
