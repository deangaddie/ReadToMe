using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The one place a ParagraphItem's text is rewritten, wherever the rewrite comes from — the item
/// menu, the generic command endpoint, or an AI book edit.
/// <para>
/// A text change invalidates the item's generated audio: the WAV speaks words the item no longer
/// has, and because the item still <em>has</em> audio it is not a Generatable item, so a "select
/// needs audio" pass skips it and the mismatch assembles into the exported m4b. Clearing
/// <c>AudioFileName</c> hands it back to the audio queue. The precedent already stands in the
/// opposite corner: a hand-flipped speaker discards the WAV, LLM stamping does not
/// (<see cref="SetItemCharacterHandler"/>, ADR-0006) — a hand edit is the same kind of explicit
/// statement that the current audio is wrong.
/// </para>
/// <para>
/// Any <c>AudioReview</c> row is <b>deleted</b>, not dismissed. The review judged audio that no
/// longer exists, so it is stale rather than resolved, and <c>Dismissed</c> means "a human decided
/// this was fine" — which would wrongly suppress the review of whatever is generated next. The item
/// returns to a clean pre-generation state: no audio, no verdict, ready to be regenerated and
/// judged afresh.
/// </para>
/// <para>
/// Unchanged text is a no-op, so a save that edits nothing keeps good audio and its verdict. The
/// accepted cost, recorded rather than hidden: editing an item purely to fix a typo or a
/// pronunciation spelling loses good audio and pays for a regeneration.
/// </para>
/// </summary>
internal static class ParagraphItemTextEdit
{
    /// <summary>
    /// Applies <paramref name="newText"/> to <paramref name="item"/>, discarding stale audio when
    /// the text actually changed. Does not save — the calling handler owns the unit of work.
    /// </summary>
    /// <returns>True when the text changed and the audio was discarded.</returns>
    public static async Task<bool> ApplyAsync(
        ProjectDbContext db, ParagraphItem item, string? newText, CancellationToken ct = default)
    {
        if (item.Text == newText) return false;

        item.Text = newText;
        item.AudioFileName = null;

        var reviews = await db.AudioReviews
            .Where(r => r.ParagraphItemId == item.Id)
            .ToListAsync(ct);
        if (reviews.Count > 0) db.AudioReviews.RemoveRange(reviews);

        return true;
    }
}

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

/// <summary>
/// Rewrites one item's text. The rewrite discards the item's stale audio and any verdict on it —
/// see <see cref="ParagraphItemTextEdit"/>. This is also the handler behind
/// <c>POST /api/projects/{folder}/commands</c> with <c>UpdateParagraphItemText</c>, so an agent
/// posting the command gets the same clearing without the dialog in front of it.
/// </summary>
public sealed class UpdateParagraphItemTextHandler(ProjectDbSession session) : ICommandHandler<UpdateParagraphItemTextCommand>
{
    public async Task<Guid?> HandleAsync(UpdateParagraphItemTextCommand c, CancellationToken ct)
    {
        var db = await session.OpenAsync(c.FolderId);
        var e = await db.ParagraphItems.FindAsync(c.ItemId);
        if (e == null) return null;
        if (!await ParagraphItemTextEdit.ApplyAsync(db, e, c.Text, ct)) return null;
        await db.SaveChangesAsync(ct);
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
