using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.State
{
    public record BookProjectSnapshot(
        string? Filename,
        bool HasContent,
        IReadOnlyList<Volume> Volumes,
        List<Character> Characters,
        int TotalParts,
        int TotalChapters,
        HashSet<Guid> SelectableNodeIds,
        IReadOnlyDictionary<Guid, int> NodeCharacterParagraphCounts,
        bool NarratorOnlyMode,
        IReadOnlyDictionary<Guid, int> AudioNodeCounts,
        List<(Guid ParagraphItemId, AudioReviewInfo Info)> AudioReviews,
        IReadOnlyList<ParagraphStatusSeedRow> NodeStatusSeed
    );

    public interface IBookProjectLoader
    {
        Task<BookProjectSnapshot> LoadSnapshotAsync(ProjectFolderId folderId, CancellationToken ct = default);
    }
}
