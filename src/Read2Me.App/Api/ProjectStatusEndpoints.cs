using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.Api
{
    public sealed record ItemCountsDto(int Total, int WithAudio, int Unattributed);

    /// <summary>Paragraphs still needing attribution, plus what the queue has in flight for this project.</summary>
    public sealed record AttributionCountsDto(int Remaining, bool Processing, int Queued);

    /// <summary>Paragraphs with at least one item still missing audio.</summary>
    public sealed record AudioCountsDto(int Remaining);

    /// <summary>
    /// The whole-project roll-up behind the pipeline stepper and the reader tree badges.
    /// <see cref="Nodes"/> is keyed by Volume/Part/Chapter id and is the same map the live hub's
    /// <c>nodeStatus</c> deltas patch; <see cref="VolumeIds"/> are in book order.
    /// </summary>
    public sealed record ProjectStatusDto(
        bool HasContent,
        int Characters,
        int CharactersWithLines,
        int ReadyVoices,
        ItemCountsDto Items,
        AttributionCountsDto Attribution,
        AudioCountsDto Audio,
        int Review,
        IReadOnlyList<Guid> VolumeIds,
        IReadOnlyDictionary<string, NodeStatusSummary> Nodes,
        long Revision);

    public static class ProjectStatusEndpoints
    {
        public static void MapProjectStatusEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/status", GetProjectStatusAsync)
                .WithSummary("Whole-project roll-up: content, cast and voice counts, item/attribution/audio/review remaining, per-node summaries and the book revision they were read at.");
            endpoints.MapGet("/api/projects/{folder}/nodes/{level}/{id:guid}/status", GetNodeStatusAsync)
                .WithSummary("Roll-up for one volume/part/chapter: attribution/audio/review paragraphs remaining and attribution in flight. A node with no speech paragraphs answers zeros.");
        }

        private static async Task<IResult> GetProjectStatusAsync(
            string folder, IFileSystem fs, IBookContentReader content, IAudioItemReader audio,
            ICharacterReader characters, NodeStatusService nodeStatus, BookRevisionSequence revisions,
            CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            // Stamped before the reads, like a projection snapshot: a mutation that commits during
            // them carries a higher revision, so the client's gap check still refetches.
            var revision = revisions.Current(folderId);
            var seed = await SeedAsync(folderId, audio, nodeStatus);
            var volumeIds = (await content.GetVolumesAsync(folderId)).Select(v => v.Id).ToList();
            var itemCounts = await audio.GetNodeAudioItemCountsAsync(folderId);
            var cast = await characters.GetCastCountsAsync(folderId, ct);

            var nodes = nodeStatus.NodeIds(folderId)
                .ToDictionary(id => id.ToString(), id => nodeStatus.StatusForNode(folderId, id));
            // Every paragraph sits under exactly one volume, so the volume summaries partition the book.
            var volumes = volumeIds
                .Select(id => nodes.GetValueOrDefault(id.ToString()))
                .ToList();

            var total = volumeIds.Sum(id => itemCounts.GetValueOrDefault(id));
            return Results.Ok(new ProjectStatusDto(
                HasContent: await content.HasBookContentAsync(folderId),
                Characters: cast.Characters,
                CharactersWithLines: cast.CharactersWithLines,
                ReadyVoices: cast.ReadyVoices,
                Items: new ItemCountsDto(total, total - seed.Sum(r => r.MissingAudio), seed.Sum(r => r.Unattributed)),
                Attribution: new AttributionCountsDto(
                    seed.Count(r => r.Unattributed > 0),
                    volumes.Any(v => v.AttributionProcessing),
                    volumes.Sum(v => v.AttributionQueued)),
                Audio: new AudioCountsDto(nodeStatus.AudioRemainingForFolder(folderId)),
                Review: seed.Count(r => r.Review > 0),
                VolumeIds: volumeIds,
                Nodes: nodes,
                Revision: revision));
        }

        private static async Task<IResult> GetNodeStatusAsync(
            string folder, string level, Guid id, IFileSystem fs, IAudioItemReader audio, NodeStatusService nodeStatus)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (!Enum.TryParse<BookNodeLevel>(level, ignoreCase: true, out _))
                return Results.Problem($"Unknown level '{level}'. Expected volume, part or chapter.",
                    statusCode: StatusCodes.Status400BadRequest);

            await SeedAsync(folderId, audio, nodeStatus);
            return Results.Ok(nodeStatus.StatusForNode(folderId, id));
        }

        /// <summary>
        /// <see cref="NodeStatusService"/> holds persisted counts only as fresh as its last seed, and
        /// only the Blazor book view seeds it otherwise. Reseeding from the database here makes every
        /// read current and, through <c>Changed</c>, pushes the corrected roll-ups to hub members.
        /// </summary>
        private static async Task<IReadOnlyList<ParagraphStatusSeedRow>> SeedAsync(
            ProjectFolderId folderId, IAudioItemReader audio, NodeStatusService nodeStatus)
        {
            var seed = await audio.GetNodeStatusSeedAsync(folderId);
            nodeStatus.Seed(folderId, seed);
            return seed;
        }
    }
}
