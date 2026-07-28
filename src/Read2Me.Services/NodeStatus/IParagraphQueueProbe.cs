using Read2Me.Core.Models;
using Read2Me.Services.Characters;

namespace Read2Me.Services.NodeStatus
{
    /// <summary>
    /// The one fact <see cref="NodeStatusService"/> needs from a work queue: is this paragraph
    /// in flight right now, and did anything about that change? Owned by this namespace and
    /// implemented by the character queue, so the roll-up depends on an interface it defines
    /// rather than on the queue module itself.
    /// </summary>
    /// <remarks>
    /// The <see cref="ParagraphQueueStatus"/> enum is the queue's own vocabulary and is shared
    /// as-is; nothing else about the queue is visible here.
    /// </remarks>
    public interface IParagraphQueueProbe
    {
        ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId);

        event Action? Changed;
    }
}
