using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.History;

namespace Read2Me.Services.UseCases
{
    /// <summary>How many an enqueue queued; with a <paramref name="Refusal"/>, none were.</summary>
    public readonly record struct EnqueueOutcome(int Enqueued, ActivityRefusal? Refusal = null);

    /// <summary>
    /// Node-scoped enqueue recipes shared by the UI and the agent API: resolve the
    /// paragraphs/items under a node, order them by book position, hand them to the
    /// singleton queues. The queues dedupe (TryMarkQueued), so re-enqueueing is safe.
    /// Each answers how many it queued, or the <see cref="ProjectActivity"/> gate's refusal when
    /// the queue refused them.
    /// </summary>
    public class EnqueueUseCases(
        ICharacterReader characterReader,
        IBookContentReader bookReader,
        IAudioItemReader audioReader,
        CharacterQueueService characterQueue,
        AudioQueueService audioQueue)
    {
        public virtual async Task<EnqueueOutcome> EnqueueAttributionAsync(
            ProjectFolderId folder, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = true)
        {
            var refs = await characterReader.GetCharacterParagraphsAsync(folder, level, nodeId, unprocessedOnly);
            return await EnqueueAttributionAsync(folder, refs);
        }

        /// <summary>
        /// The web reader's selection: an explicit id list rather than a node. Ids that are not
        /// Character paragraphs are dropped by the read, so the answer counts what was queued.
        /// </summary>
        public virtual async Task<EnqueueOutcome> EnqueueAttributionAsync(ProjectFolderId folder, IReadOnlyList<Guid> paragraphIds)
        {
            var refs = await characterReader.GetCharacterParagraphRefsAsync(folder, paragraphIds);
            return await EnqueueAttributionAsync(folder, refs);
        }

        private async Task<EnqueueOutcome> EnqueueAttributionAsync(ProjectFolderId folder, List<CharacterParagraphRef> refs)
        {
            if (refs.Count == 0)
                return new(0);

            var ancestry = refs.ToDictionary(r => r.ParagraphId);
            var ordered = await bookReader.GetOrderedParagraphsAsync(folder, ancestry.Keys);
            var items = ordered.Select(p =>
            {
                var anc = ancestry[p.ParagraphId];
                return new QueuedParagraph(folder, p.ParagraphId, p.Preview, anc.ChapterId, anc.PartId, anc.VolumeId);
            }).ToList();

            return characterQueue.Enqueue(items) is { } refusal ? new(0, refusal) : new(items.Count);
        }

        public virtual async Task<EnqueueOutcome> EnqueueAudioAsync(
            ProjectFolderId folder, BookNodeLevel level, Guid nodeId,
            bool needsAudioOnly = true, bool narratorOnlyMode = false)
        {
            var refs = await audioReader.GetAudioItemRefsAsync(folder, level, nodeId, needsAudioOnly, narratorOnlyMode);
            if (refs.Count == 0)
                return new(0);

            return await EnqueueAudioAsync(folder, refs.Select(r => r.ParagraphItemId).ToList());
        }

        /// <summary>
        /// The web reader's audio selection or a single retry: an explicit item id list rather than a
        /// node. Ids that name nothing drop out of the ordering read, so the answer counts what was queued.
        /// </summary>
        public virtual async Task<EnqueueOutcome> EnqueueAudioAsync(ProjectFolderId folder, IReadOnlyList<Guid> itemIds)
        {
            if (itemIds.Count == 0)
                return new(0);

            var ordered = await audioReader.GetOrderedAudioItemRefsAsync(folder, itemIds);
            return audioQueue.Enqueue(ordered.Select(r => new QueuedAudioItem(folder, r))) is { } refusal
                ? new(0, refusal)
                : new(ordered.Count);
        }
    }
}
