using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;

namespace Read2Me.Services.UseCases
{
    /// <summary>
    /// Node-scoped enqueue recipes shared by the UI and the agent API: resolve the
    /// paragraphs/items under a node, order them by book position, hand them to the
    /// singleton queues. The queues dedupe (TryMarkQueued), so re-enqueueing is safe.
    /// </summary>
    public class EnqueueUseCases(
        ICharacterReader characterReader,
        IBookContentReader bookReader,
        IAudioItemReader audioReader,
        CharacterQueueService characterQueue,
        AudioQueueService audioQueue)
    {
        public virtual async Task<int> EnqueueAttributionAsync(
            ProjectFolderId folder, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = true)
        {
            var refs = await characterReader.GetCharacterParagraphsAsync(folder, level, nodeId, unprocessedOnly);
            if (refs.Count == 0)
                return 0;

            var ancestry = refs.ToDictionary(r => r.ParagraphId);
            var ordered = await bookReader.GetOrderedParagraphsAsync(folder, ancestry.Keys);
            var items = ordered.Select(p =>
            {
                var anc = ancestry[p.ParagraphId];
                return new QueuedParagraph(folder, p.ParagraphId, p.Preview, anc.ChapterId, anc.PartId, anc.VolumeId);
            }).ToList();

            characterQueue.Enqueue(items);
            return items.Count;
        }

        public virtual async Task<int> EnqueueAudioAsync(
            ProjectFolderId folder, BookNodeLevel level, Guid nodeId,
            bool needsAudioOnly = true, bool narratorOnlyMode = false)
        {
            var refs = await audioReader.GetAudioItemRefsAsync(folder, level, nodeId, needsAudioOnly, narratorOnlyMode);
            if (refs.Count == 0)
                return 0;

            var ordered = await audioReader.GetOrderedAudioItemRefsAsync(folder, refs.Select(r => r.ParagraphItemId));
            audioQueue.Enqueue(folder, ordered);
            return ordered.Count;
        }
    }
}
