using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Voice;

/// <summary>
/// Loads the <see cref="NodeOrderTables"/> a set of rules' anchors need: one query per anchor
/// level that any rule uses, none for levels no rule touches. Shared by the item resolver and the
/// chapter preview so both read anchors the same way.
/// </summary>
public static class NodeOrderTableLoader
{
    public static async Task<NodeOrderTables> LoadAsync(
        ProjectDbContext db, IReadOnlyCollection<VoiceRule> rules, CancellationToken ct)
    {
        var volumeIds     = new HashSet<Guid>();
        var partIds       = new HashSet<Guid>();
        var chapterIds    = new HashSet<Guid>();
        var paragraphIds  = new HashSet<Guid>();
        var anchorItemIds = new HashSet<Guid>();

        foreach (var rule in rules)
        {
            CollectId(rule.FromLevel, rule.FromNodeId, volumeIds, partIds, chapterIds, paragraphIds, anchorItemIds);
            CollectId(rule.ToLevel,   rule.ToNodeId,   volumeIds, partIds, chapterIds, paragraphIds, anchorItemIds);
        }

        var volOrders = volumeIds.Count > 0
            ? await db.Volumes.AsNoTracking()
                .Where(v => volumeIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Order })
                .ToDictionaryAsync(v => v.Id, v => v.Order, ct)
            : new Dictionary<Guid, string>();

        Dictionary<Guid, (string VolOrder, string PartOrder)> partOrders = new();
        if (partIds.Count > 0)
        {
            var rows = await db.Parts.AsNoTracking()
                .Where(p => partIds.Contains(p.Id))
                .Select(p => new { p.Id, PartOrder = p.Order, VolumeOrder = p.Volume.Order })
                .ToListAsync(ct);
            foreach (var r in rows) partOrders[r.Id] = (r.VolumeOrder, r.PartOrder);
        }

        Dictionary<Guid, (string VolOrder, string PartOrder, string ChOrder)> chapterOrders = new();
        if (chapterIds.Count > 0)
        {
            var rows = await db.Chapters.AsNoTracking()
                .Where(c => chapterIds.Contains(c.Id))
                .Select(c => new { c.Id, ChOrder = c.Order, PartOrder = c.Part.Order, VolumeOrder = c.Part.Volume.Order })
                .ToListAsync(ct);
            foreach (var r in rows) chapterOrders[r.Id] = (r.VolumeOrder, r.PartOrder, r.ChOrder);
        }

        Dictionary<Guid, (string VolOrder, string PartOrder, string ChOrder, string ParaOrder)> paraOrders = new();
        if (paragraphIds.Count > 0)
        {
            var rows = await db.Paragraphs.AsNoTracking()
                .Where(p => paragraphIds.Contains(p.Id))
                .Select(p => new { p.Id, ParaOrder = p.Order, ChOrder = p.Chapter.Order, PartOrder = p.Chapter.Part.Order, VolumeOrder = p.Chapter.Part.Volume.Order })
                .ToListAsync(ct);
            foreach (var r in rows) paraOrders[r.Id] = (r.VolumeOrder, r.PartOrder, r.ChOrder, r.ParaOrder);
        }

        Dictionary<Guid, StoryPosition> anchorItemPositions = new();
        if (anchorItemIds.Count > 0)
        {
            var rows = await db.ParagraphItems.AsNoTracking()
                .Where(pi => anchorItemIds.Contains(pi.Id))
                .Select(pi => new
                {
                    pi.Id,
                    ItemOrder   = pi.Order,
                    ParaOrder   = pi.Paragraph.Order,
                    ChOrder     = pi.Paragraph.Chapter.Order,
                    PartOrder   = pi.Paragraph.Chapter.Part.Order,
                    VolumeOrder = pi.Paragraph.Chapter.Part.Volume.Order
                })
                .ToListAsync(ct);
            foreach (var r in rows)
                anchorItemPositions[r.Id] = new StoryPosition(r.VolumeOrder, r.PartOrder, r.ChOrder, r.ParaOrder, r.ItemOrder);
        }

        return new NodeOrderTables(volOrders, partOrders, chapterOrders, paraOrders, anchorItemPositions);
    }

    private static void CollectId(
        VoiceAnchorLevel? level, Guid? nodeId,
        HashSet<Guid> volumeIds, HashSet<Guid> partIds, HashSet<Guid> chapterIds,
        HashSet<Guid> paragraphIds, HashSet<Guid> itemIds)
    {
        if (level is null || nodeId is null) return;
        switch (level.Value)
        {
            case VoiceAnchorLevel.Volume:        volumeIds.Add(nodeId.Value);    break;
            case VoiceAnchorLevel.Part:          partIds.Add(nodeId.Value);      break;
            case VoiceAnchorLevel.Chapter:       chapterIds.Add(nodeId.Value);   break;
            case VoiceAnchorLevel.Paragraph:     paragraphIds.Add(nodeId.Value); break;
            case VoiceAnchorLevel.ParagraphItem: itemIds.Add(nodeId.Value);      break;
        }
    }
}
