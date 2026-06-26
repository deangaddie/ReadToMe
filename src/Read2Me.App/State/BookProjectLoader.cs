using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Services;

namespace Read2Me.App.State
{
    public class BookProjectLoader(IProjectReader reader) : IBookProjectLoader
    {
        public async Task<BookProjectSnapshot> LoadSnapshotAsync(
            ProjectFolderId folderId, CancellationToken ct = default)
        {
            var overview = await reader.GetBookOverviewAsync(folderId);
            var project = await reader.GetProjectAsync(folderId);

            var audioNodeCounts = overview.HasContent
                ? await reader.GetNodeAudioItemCountsAsync(folderId)
                : new Dictionary<Guid, int>();

            var audioReviews = overview.HasContent
                ? await reader.GetAudioReviewsAsync(folderId)
                : [];

            var nodeStatusSeed = await reader.GetNodeStatusSeedAsync(folderId);

            return new BookProjectSnapshot(
                Filename: overview.Filename,
                HasContent: overview.HasContent,
                Volumes: overview.Volumes,
                Characters: overview.Characters.ToList(),
                TotalParts: overview.TotalParts,
                TotalChapters: overview.TotalChapters,
                SelectableNodeIds: overview.SelectableNodeIds,
                NodeCharacterParagraphCounts: overview.NodeCharacterParagraphCounts,
                NarratorOnlyMode: project?.NarratorOnlyMode ?? false,
                AudioNodeCounts: audioNodeCounts,
                AudioReviews: audioReviews,
                NodeStatusSeed: nodeStatusSeed
            );
        }
    }
}
