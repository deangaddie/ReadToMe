using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// A busy answer for which every project is idle, so a test can drive restoring through a real
    /// <see cref="ProjectActivity"/> without the queues' busy sources taking part.
    /// </summary>
    public sealed class NeverBusy : IProjectBusyAnswer
    {
        public static readonly NeverBusy Instance = new();

        public ProjectBusy? For(ProjectFolderId folder) => null;

        /// <summary>A gate of its own whose projects are never busy.</summary>
        public static ProjectActivity Activity() => new(() => Instance);
    }
}
