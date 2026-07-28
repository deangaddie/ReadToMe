using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.NodeStatus;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// Hand-set queue state for the node roll-up: nothing is in flight unless a test says so.
    /// </summary>
    public sealed class FakeParagraphQueueProbe : IParagraphQueueProbe
    {
        private readonly Dictionary<(ProjectFolderId Folder, Guid ParagraphId), ParagraphQueueStatus> _statuses = new();

        public event Action? Changed;

        public ParagraphQueueStatus? StatusOf(ProjectFolderId folder, Guid paragraphId)
            => _statuses.TryGetValue((folder, paragraphId), out var s) ? s : null;

        public void Set(ProjectFolderId folder, Guid paragraphId, ParagraphQueueStatus status)
        {
            _statuses[(folder, paragraphId)] = status;
            Changed?.Invoke();
        }

        public void Clear(ProjectFolderId folder, Guid paragraphId)
        {
            _statuses.Remove((folder, paragraphId));
            Changed?.Invoke();
        }

        /// <summary>Fires <see cref="Changed"/> without altering any status.</summary>
        public void RaiseChanged() => Changed?.Invoke();
    }
}
