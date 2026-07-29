using System.Collections.Generic;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The audio queue as its processor uses it: take the work, signal that an item has gone in
    /// flight, and apply the one <see cref="Disposition"/> the decision produced.
    /// <para>
    /// Three mutators, not one per outcome, and the mirror of <see cref="Characters.ICharacterQueue"/>
    /// minus what this queue has no use for: there is no <c>MarkDeferred</c> (nothing here holds an
    /// item back for a later escalation step), no <c>DrainAll</c> (the worker hands over one item at a
    /// time) and no item cancellation token (this queue cancels by completing its channel).
    /// </para>
    /// <para>
    /// Both <see cref="Enqueue"/> and <see cref="MarkProcessing"/> take the payload rather than a
    /// folder + item pair, so <see cref="AttemptState"/> has exactly one construction site.
    /// </para>
    /// </summary>
    public interface IAudioQueue
    {
        void Enqueue(IEnumerable<QueuedAudioItem> items);

        /// <summary>The item has gone in flight.</summary>
        void MarkProcessing(QueuedAudioItem item);

        /// <summary>
        /// Runs the decided transition. Total: every <see cref="Disposition"/> member executes and no
        /// arm throws. Performs no work — resolving, generating and recording stay with the processor.
        /// </summary>
        void Apply(QueuedAudioItem item, Disposition disposition);
    }
}
