using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services.Events;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Stamps an attribution answer onto a paragraph's existing items. Item boundaries are frozen
/// (ADR 0005): nothing here inserts, deletes, reorders or retypes an item, and <c>Text</c> is never
/// rewritten — so generated audio can only be invalidated by a speaker change, never by a re-split.
/// <para>
/// Each <see cref="ItemAttribution"/> is matched by id against this paragraph's items; ids that
/// belong to another paragraph, or no longer exist, are ignored (the answer may be stale — the queue
/// is asynchronous and the user may have edited items since the ask). A null <c>CharacterId</c>
/// means "unknown" and leaves any existing stamp alone, while <c>VoiceInstructions</c> are
/// overwritten unconditionally — including to null: the answer is the whole truth about how an item
/// it names should be read.
/// </para>
/// <para>
/// Only Character items are stamped. Pause items carry no speech at all, and spec §2 has the caller
/// drop answers on narration indices — enforced again here so a stray narration id cannot leave a
/// narration item pointing at a character, the audio-inert state
/// <see cref="SetItemCharacterHandler"/> warns about.
/// </para>
/// </summary>
public sealed class AttributeItemsHandler(
    ProjectDbSession session,
    EventBroadcaster<ParagraphItemsChanged> events) : ICommandHandler<AttributeItemsCommand>
{
    public async Task<Guid?> HandleAsync(AttributeItemsCommand c, CancellationToken ct)
    {
        if (c.Items.Count == 0) return null;

        var db = await session.OpenAsync(c.FolderId);
        if (!await db.Paragraphs.AnyAsync(p => p.Id == c.ParagraphId, ct)) return null;

        var items = await db.ParagraphItems
            .Where(i => i.ParagraphId == c.ParagraphId)
            .ToDictionaryAsync(i => i.Id, ct);

        var stamped = false;
        foreach (var attribution in c.Items)
        {
            if (!items.TryGetValue(attribution.ItemId, out var item)) continue;
            if (item.ItemType != ParagraphItemType.Character) continue;

            if (attribution.CharacterId.HasValue) item.CharacterId = attribution.CharacterId;
            item.VoiceInstructions = attribution.VoiceInstructions;
            stamped = true;
        }

        // An answer that matched nothing — every id stale or foreign — changed no row, so there is
        // nothing for the UI to redraw.
        if (!stamped) return null;

        await db.SaveChangesAsync(ct);
        events.Publish(new ParagraphItemsChanged(c.FolderId, c.ParagraphId));
        return null;
    }
}
