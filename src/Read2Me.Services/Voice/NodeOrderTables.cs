using Read2Me.Core.Models;

namespace Read2Me.Services.Voice;

public readonly record struct NodeOrderTables(
    Dictionary<Guid, string> VolOrders,
    Dictionary<Guid, (string VolOrder, string PartOrder)> PartOrders,
    Dictionary<Guid, (string VolOrder, string PartOrder, string ChOrder)> ChapterOrders,
    Dictionary<Guid, (string VolOrder, string PartOrder, string ChOrder, string ParaOrder)> ParaOrders,
    Dictionary<Guid, StoryPosition> AnchorItemPositions)
{
    public bool TryResolve(VoiceAnchorLevel level, Guid nodeId, bool isMin, out StoryPosition position)
    {
        var min = StoryPosition.MinKey;
        var max = StoryPosition.MaxKey;

        switch (level)
        {
            case VoiceAnchorLevel.Volume:
                if (!VolOrders.TryGetValue(nodeId, out var volOrder))
                { position = default; return false; }
                position = isMin
                    ? new StoryPosition(volOrder, min, min, min, min)
                    : new StoryPosition(volOrder, max, max, max, max);
                return true;

            case VoiceAnchorLevel.Part:
                if (!PartOrders.TryGetValue(nodeId, out var partRow))
                { position = default; return false; }
                position = isMin
                    ? new StoryPosition(partRow.VolOrder, partRow.PartOrder, min, min, min)
                    : new StoryPosition(partRow.VolOrder, partRow.PartOrder, max, max, max);
                return true;

            case VoiceAnchorLevel.Chapter:
                if (!ChapterOrders.TryGetValue(nodeId, out var chRow))
                { position = default; return false; }
                position = isMin
                    ? new StoryPosition(chRow.VolOrder, chRow.PartOrder, chRow.ChOrder, min, min)
                    : new StoryPosition(chRow.VolOrder, chRow.PartOrder, chRow.ChOrder, max, max);
                return true;

            case VoiceAnchorLevel.Paragraph:
                if (!ParaOrders.TryGetValue(nodeId, out var paraRow))
                { position = default; return false; }
                position = isMin
                    ? new StoryPosition(paraRow.VolOrder, paraRow.PartOrder, paraRow.ChOrder, paraRow.ParaOrder, min)
                    : new StoryPosition(paraRow.VolOrder, paraRow.PartOrder, paraRow.ChOrder, paraRow.ParaOrder, max);
                return true;

            case VoiceAnchorLevel.ParagraphItem:
                if (!AnchorItemPositions.TryGetValue(nodeId, out var itemPos))
                { position = default; return false; }
                position = itemPos;
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }
}
