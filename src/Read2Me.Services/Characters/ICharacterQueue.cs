using System;
using System.Collections.Generic;
using System.Threading;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// The character queue as its processor uses it: drain the work, signal progress, and apply the
    /// one <see cref="Disposition"/> the decision produced.
    /// <para>
    /// Four mutators, not one per outcome. <c>Enqueue</c> is not a disposition (nothing has been
    /// worked yet); <see cref="MarkProcessing"/> and <see cref="MarkDeferred"/> are chain
    /// <i>progress</i> signals fired mid-run, when no outcome exists — folding them into
    /// <see cref="Apply"/> would mean inventing non-terminal dispositions and giving up the guarantee
    /// that every <see cref="Disposition"/> member executes.
    /// </para>
    /// <para>
    /// Not narrowed further to a write-only sink: the processor also needs <see cref="DrainAll"/>,
    /// <see cref="MarkDeferred"/> and <see cref="ItemCancellationToken"/>, so no caller could depend
    /// on the sink alone.
    /// </para>
    /// </summary>
    public interface ICharacterQueue
    {
        /// <summary>Cancelled when the queue is cleared, so in-flight work stops with it.</summary>
        CancellationToken ItemCancellationToken { get; }

        void Enqueue(IEnumerable<QueuedParagraph> paragraphs);

        /// <summary>
        /// Returns <paramref name="first"/> plus every remaining queued paragraph, drained from the
        /// head of the queue in book order. Marks, resolves and requeues nothing.
        /// </summary>
        IReadOnlyList<QueuedParagraph> DrainAll(QueuedParagraph first);

        /// <summary>The item's chunk has gone in flight.</summary>
        void MarkProcessing(QueuedParagraph item);

        /// <summary>
        /// The chain still owns the item and will re-drive it on a later escalation step: it returns
        /// to Queued without re-entering the channel, and spends no retry budget.
        /// </summary>
        void MarkDeferred(QueuedParagraph item);

        /// <summary>
        /// Runs the decided transition. Total: every <see cref="Disposition"/> member executes and no
        /// arm throws. Performs no work — apply and probe stay with the processor.
        /// </summary>
        void Apply(QueuedParagraph item, Disposition disposition);
    }
}
