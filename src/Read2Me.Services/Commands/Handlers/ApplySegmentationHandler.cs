using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Core.Utils;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// Applies a paragraph's full segment list: text items are reconciled against the segments, so a
/// segment whose type and normalized text still match an existing item keeps that item — id, order
/// key, and generated audio survive. Everything else is replaced. Pause items are never touched.
/// <para>
/// Speaker names are already resolved to ids by the caller; a null id means "unknown", which never
/// erases an existing stamp. A matched item keeps its stored <c>Text</c> verbatim (the match is
/// normalization-tolerant, and rewriting the text would invalidate the audio that item still
/// carries), but its <c>VoiceInstructions</c> are overwritten — including with null, since the
/// segment list is the whole truth about how the paragraph should be read. A new narration item is
/// stamped with the narrator, as everywhere else narration items are created.
/// </para>
/// </summary>
public sealed class ApplySegmentationHandler(
    ProjectDbSession session,
    EventBroadcaster<ParagraphItemsChanged> events) : ICommandHandler<ApplySegmentationCommand>
{
    public async Task<Guid?> HandleAsync(ApplySegmentationCommand c, CancellationToken ct)
    {
        if (c.Segments.Count == 0) return null;

        var db = await session.OpenAsync(c.FolderId);
        if (!await db.Paragraphs.AnyAsync(p => p.Id == c.ParagraphId, ct)) return null;

        var items = await db.ParagraphItems
            .Where(i => i.ParagraphId == c.ParagraphId)
            .ToListAsync(ct);
        items.Sort((a, b) => string.CompareOrdinal(a.Order, b.Order));

        var textItems = items.Where(IsTextItem).ToList();
        var claimed = MatchPositionally(c.Segments, textItems);

        var removed = textItems.Where((_, k) => !claimed.Contains(k)).ToList();
        db.ParagraphItems.RemoveRange(removed);

        var survivorKeys = items.Except(removed)
            .Select(i => i.Order)
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();

        // Insertions live inside the text region: they start after whatever precedes the first text
        // item and stop at the next surviving item, so a leading pause keeps its place. A pause
        // interleaved between text items is the one ambiguous case — it can only end up on one side
        // of a fully re-split paragraph, and it lands after the new text.
        var prevKey = textItems.Count == 0
            ? items.LastOrDefault()?.Order
            : items.TakeWhile(i => !IsTextItem(i)).LastOrDefault()?.Order;

        for (var s = 0; s < c.Segments.Count; s++)
        {
            var segment = c.Segments[s];
            if (claimed.ItemFor(s) is { } k)
            {
                var item = textItems[k];
                if (segment.CharacterId.HasValue) item.CharacterId = segment.CharacterId;
                item.VoiceInstructions = segment.VoiceInstructions;
                prevKey = item.Order;
                continue;
            }

            var nextKey = survivorKeys.FirstOrDefault(o => prevKey == null || string.CompareOrdinal(o, prevKey) > 0);
            var order = OrderHelper.GetBetween(prevKey, nextKey);
            var itemType = ToItemType(segment.Type);
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = c.ParagraphId,
                Order = order,
                ItemType = itemType,
                Text = segment.Text,
                CharacterId = segment.CharacterId ?? (itemType == ParagraphItemType.Narration ? ProjectDbContext.NarratorId : null),
                VoiceInstructions = segment.VoiceInstructions,
            });
            prevKey = order;
        }

        await db.SaveChangesAsync(ct);
        events.Publish(new ParagraphItemsChanged(c.FolderId, c.ParagraphId));
        return null;
    }

    /// <summary>
    /// Greedy positional walk: each segment claims the first still-unclaimed item at or after the
    /// last claim that has the same type and the same normalized text. Items nobody claims are
    /// replaced.
    /// </summary>
    private static Claims MatchPositionally(IReadOnlyList<SegmentSpec> segments, List<ParagraphItem> textItems)
    {
        var perSegment = new int?[segments.Count];
        var claimedItems = new HashSet<int>();
        var from = 0;

        for (var s = 0; s < segments.Count; s++)
        {
            var wantedType = ToItemType(segments[s].Type);
            var wantedText = SegmentTextNormalizer.Normalize(segments[s].Text);

            for (var k = from; k < textItems.Count; k++)
            {
                if (textItems[k].ItemType != wantedType) continue;
                if (SegmentTextNormalizer.Normalize(textItems[k].Text ?? string.Empty) != wantedText) continue;
                perSegment[s] = k;
                claimedItems.Add(k);
                from = k + 1;
                break;
            }
        }

        return new Claims(perSegment, claimedItems);
    }

    private readonly record struct Claims(int?[] PerSegment, HashSet<int> ClaimedItems)
    {
        public int? ItemFor(int segment) => PerSegment[segment];
        public bool Contains(int itemIndex) => ClaimedItems.Contains(itemIndex);
    }

    private static ParagraphItemType ToItemType(SegmentItemType type) =>
        type == SegmentItemType.Narration ? ParagraphItemType.Narration : ParagraphItemType.Character;

    private static bool IsTextItem(ParagraphItem i) =>
        i.ItemType is ParagraphItemType.Narration or ParagraphItemType.Character;
}
