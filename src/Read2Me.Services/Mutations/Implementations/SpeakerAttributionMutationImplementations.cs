using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the speaker attribution family shares (ADR 0007). Every mutation here restamps existing
/// items and nothing else — item boundaries are frozen (ADR 0005) — so each one names exactly the
/// Paragraphs and ParagraphItems it moved.
/// <para>
/// That exactness is the whole reason this family is separate from the structural ones. It is the
/// Book's highest-frequency write: a queue works through a chapter a paragraph at a time, and a
/// reader that had to rebuild its expanded branches for each answer would spend the run rereading a
/// Book that moved by one row.
/// </para>
/// </summary>
internal static class SpeakerEffects
{
    /// <summary>
    /// A restamp of known items. <paramref name="invalidatedAudio"/> is what separates a hand-flip
    /// from the queue's answer: a person correcting a speaker is saying the generated audio is in
    /// the wrong voice, so it goes; the queue's attribution leaves audio alone (ADR-0006).
    /// </summary>
    public static BookMutationEffects Stamped(
        IReadOnlyList<Guid> paragraphIds, IReadOnlyList<Guid> itemIds, bool invalidatedAudio) => new()
    {
        Scope = BookMutationScope.Exact,
        Facets = invalidatedAudio ? BookFacets.Attribution | BookFacets.Audio : BookFacets.Attribution,
        ParagraphIds = paragraphIds,
        ParagraphItemIds = itemIds,
    };

    public static BookMutationRejectedException NotFound(string what, Guid id) =>
        new(BookMutationRejection.NotFound, $"No {what} {id} to stamp a speaker on.");
}

/// <summary>
/// Stamps one item's speaker — any speaker, on any speech item. Narration is a speaker, not an item
/// type (ADR-0006): stamping the narrator sentinel makes the item narration, stamping a character
/// makes it that character's line, and clearing it hands the item back to the attribution queue as
/// unattributed dialog.
/// <para>
/// A pause is nobody's: nothing reads a stamped pause and every reader filters it out, so the stamp
/// would sit there invisible and untrue. Asking for one is a legal gesture that changes nothing
/// rather than a refusal — the same answer the item already on that speaker gets.
/// </para>
/// </summary>
public sealed class SetItemSpeakerMutationImplementation : IBookMutationImplementation<SetItemSpeakerMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetItemSpeakerMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var item = await db.ParagraphItems.FirstOrDefaultAsync(i => i.Id == mutation.ItemId, ct)
            ?? throw SpeakerEffects.NotFound("paragraph item", mutation.ItemId);

        if (ParagraphItemKinds.IsPause(item.ItemType)) return BookMutationEffects.Nothing;
        if (item.CharacterId == mutation.CharacterId) return BookMutationEffects.Nothing;

        // Reported only when there was audio to lose: the facets say what this write actually did,
        // and an item that had none gives a reader nothing to reread.
        var hadAudio = item.AudioFileName is not null;

        item.CharacterId = mutation.CharacterId;
        item.AudioFileName = null;   // a hand-flip discards the item's audio (ADR-0006)

        return SpeakerEffects.Stamped([item.ParagraphId], [item.Id], hadAudio);
    }
}

/// <summary>
/// Stamps a speaker across a Paragraph, sweeping its speech items *except* the narration — the same
/// line the old <c>ItemType == Character</c> filter drew, now expressed against the speaker
/// (ADR-0006). Preserving narration is what stops a one-gesture speaker fix destroying the
/// paragraph's narration/dialog split.
/// <para>
/// One exception, and it is what makes that gesture reversible: a Paragraph with <em>no</em> dialog
/// left — every speech item narration, usually because the user just assigned the whole paragraph to
/// the narrator — sweeps its narration instead.
/// </para>
/// </summary>
public sealed class SetParagraphSpeakerMutationImplementation
    : IBookMutationImplementation<SetParagraphSpeakerMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetParagraphSpeakerMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (!await db.Paragraphs.AnyAsync(p => p.Id == mutation.ParagraphId, ct))
            throw SpeakerEffects.NotFound("paragraph", mutation.ParagraphId);

        var speech = await db.ParagraphItems
            .Where(i => i.ParagraphId == mutation.ParagraphId)
            .Where(ParagraphItemKinds.IsSpeechExpression)
            .ToListAsync(ct);

        var dialog = speech.Where(NarrationRule.IsDialog).ToList();
        var sweeping = dialog.Count > 0 ? dialog : speech;

        var stamped = new List<Guid>();
        var invalidatedAudio = false;
        foreach (var item in sweeping)
        {
            var changed = false;

            // Only an item this gesture actually moves loses its audio; one already on the target
            // speaker keeps what it has, which is what makes assigning the narrator idempotent.
            if (item.CharacterId != mutation.CharacterId)
            {
                invalidatedAudio |= item.AudioFileName is not null;
                item.CharacterId = mutation.CharacterId;
                item.AudioFileName = null;
                changed = true;
            }

            if (mutation.CharacterId.HasValue && mutation.VoiceInstructions != null
                && item.VoiceInstructions != mutation.VoiceInstructions)
            {
                item.VoiceInstructions = mutation.VoiceInstructions;
                changed = true;
            }

            if (changed) stamped.Add(item.Id);
        }

        if (stamped.Count == 0) return BookMutationEffects.Nothing;

        return SpeakerEffects.Stamped([mutation.ParagraphId], stamped, invalidatedAudio);
    }
}

/// <summary>
/// The bulk sibling of <see cref="SetParagraphSpeakerMutationImplementation"/>: one set-based
/// update, no entities loaded, so a thousand-paragraph selection costs no change-tracker time. The
/// id list is not chunked — EF translates <c>Contains</c> to a single json-valued parameter at any
/// length. It sweeps the same dialog items its sibling does, so narration survives a
/// thousand-paragraph correction.
/// <para>
/// The one extra read it takes is what the receipt is made of: the items about to move, named
/// before the update makes them unfindable. <c>VoiceInstructions</c> is left alone — there is no
/// per-line instruction to spread across a selection.
/// </para>
/// </summary>
public sealed class SetParagraphsSpeakerMutationImplementation
    : IBookMutationImplementation<SetParagraphsSpeakerMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetParagraphsSpeakerMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var moving = db.ParagraphItems
            .Where(i => mutation.ParagraphIds.Contains(i.ParagraphId))
            .Where(NarrationRule.IsDialogExpression)
            .Where(i => i.CharacterId != mutation.CharacterId);

        var affected = await moving
            .Select(i => new { i.Id, i.ParagraphId, HadAudio = i.AudioFileName != null })
            .ToListAsync(ct);

        if (affected.Count == 0) return BookMutationEffects.Nothing;

        await moving.ExecuteUpdateAsync(s => s
            .SetProperty(i => i.CharacterId, mutation.CharacterId)
            .SetProperty(i => i.AudioFileName, (string?)null), ct);

        return SpeakerEffects.Stamped(
            [.. affected.Select(a => a.ParagraphId).Distinct()],
            [.. affected.Select(a => a.Id)],
            affected.Any(a => a.HadAudio));
    }
}

/// <summary>
/// Stamps the Character Queue's answer onto one Paragraph's existing items.
/// <para>
/// Each attribution is matched by id against this paragraph's items; ids that belong to another
/// paragraph, or no longer exist, are ignored — the answer may be stale, since the queue is
/// asynchronous and the user may have edited items since the ask. A null <c>CharacterId</c> means
/// "unknown" and leaves any existing stamp alone, while <c>VoiceInstructions</c> are overwritten
/// unconditionally — including to null: the answer is the whole truth about how an item it names
/// should be read.
/// </para>
/// <para>
/// Only non-narrator speech items are stamped. A pause carries no speech at all, and a
/// narrator-stamped item is one the user (or the splitter) has already settled: assigning an item to
/// the narrator is also the gesture that locks it out of re-attribution (ADR-0006).
/// </para>
/// </summary>
public sealed class AttributeParagraphItemsMutationImplementation
    : IBookMutationImplementation<AttributeParagraphItemsMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        AttributeParagraphItemsMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        if (mutation.Items.Count == 0) return BookMutationEffects.Nothing;

        if (!await db.Paragraphs.AnyAsync(p => p.Id == mutation.ParagraphId, ct))
            throw SpeakerEffects.NotFound("paragraph", mutation.ParagraphId);

        var items = await db.ParagraphItems
            .Where(i => i.ParagraphId == mutation.ParagraphId)
            .ToDictionaryAsync(i => i.Id, ct);

        var stamped = new List<Guid>();
        foreach (var attribution in mutation.Items)
        {
            if (!items.TryGetValue(attribution.ItemId, out var item)) continue;
            if (ParagraphItemKinds.IsPause(item.ItemType)) continue;
            if (NarrationRule.IsNarration(item)) continue;

            var changed = false;
            if (attribution.CharacterId.HasValue && item.CharacterId != attribution.CharacterId)
            {
                item.CharacterId = attribution.CharacterId;
                changed = true;
            }

            if (item.VoiceInstructions != attribution.VoiceInstructions)
            {
                item.VoiceInstructions = attribution.VoiceInstructions;
                changed = true;
            }

            if (changed) stamped.Add(item.Id);
        }

        // An answer that matched nothing — every id stale or foreign — or that only agreed with what
        // is already stamped changed no row: no revision, no receipt, and no Book View rereads.
        if (stamped.Count == 0) return BookMutationEffects.Nothing;

        return SpeakerEffects.Stamped([mutation.ParagraphId], stamped, invalidatedAudio: false);
    }
}
