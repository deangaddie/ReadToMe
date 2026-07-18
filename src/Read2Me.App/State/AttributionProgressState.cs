using System;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.App.State
{
    /// <summary>
    /// App-scoped signal that the attribution chain has drained a level and escalated its
    /// remaining suspect items onto a stronger config. Subscribes to the shared LLM stream
    /// broadcaster and latches the most recent <see cref="EscalationStarted"/> so the status
    /// dock can surface "escalating N items → config (step S)" in the character-queue progress
    /// row — the stream panel already shows the same event, but the progress row is visible
    /// without expanding the live stream.
    ///
    /// The latch clears when the character queue goes idle (nothing queued or processing), so a
    /// finished run doesn't leave a stale escalation label pinned in the dock.
    ///
    /// It is also the attribution queue's Throughput Run producer: watching the same queue
    /// transitions, it brackets a queue's work with <see cref="RunStarted"/>/<see cref="RunEnded"/>
    /// on the LLM bus so the throughput aggregator never learns about the queue service. The two
    /// jobs share a subscription but not a rule — the escalation latch only clears once an
    /// escalation has been latched, whereas a run brackets every queue, escalated or not.
    /// </summary>
    public sealed class AttributionProgressState : IDisposable
    {
        private readonly EventBroadcaster<LlmStreamEvent> _stream;
        private readonly CharacterQueueService _queue;

        /// <summary>True between this producer's RunStarted and its matching RunEnded.</summary>
        private bool _runActive;

        /// <summary>1-based step index of the latest escalation, or null when none is active.</summary>
        public int? Step { get; private set; }

        /// <summary>Config the latest escalation stepped onto.</summary>
        public string? ConfigName { get; private set; }

        /// <summary>Number of suspect items carried into the latest escalation step.</summary>
        public int ItemCount { get; private set; }

        /// <summary>True while an escalation label should be shown in the progress row.</summary>
        public bool HasEscalation => Step is not null;

        /// <summary>
        /// True while the shared throughput snapshot belongs to the current or most recently
        /// completed attribution queue. A later non-attribution run takes ownership away.
        /// </summary>
        public bool OwnsThroughputSnapshot { get; private set; }

        public event Action? Changed;

        public AttributionProgressState(
            EventBroadcaster<LlmStreamEvent> stream, CharacterQueueService queue)
        {
            _stream = stream;
            _queue = queue;
            _stream.Event += OnStreamEvent;
            _queue.Changed += OnQueueChanged;
        }

        private void OnStreamEvent(LlmStreamEvent e)
        {
            // Our own RunStarted is published only after _runActive becomes true. A RunStarted
            // received while idle therefore belongs to another surface, whose snapshot must not
            // be presented as attribution throughput in StatusDock.
            if (e is RunStarted && !_runActive)
                OwnsThroughputSnapshot = false;

            if (e is not EscalationStarted es) return;
            Step = es.Step;
            ConfigName = es.ConfigName;
            ItemCount = es.ItemCount;
            Changed?.Invoke();
        }

        private void OnQueueChanged()
        {
            var snap = _queue.Snapshot();
            var busy = snap.QueuedCount > 0 || snap.ProcessingCount > 0;

            // Throughput Run boundary: work appearing on an idle queue opens the run, and the
            // queue draining closes it — whether it drained by finishing, failing or being
            // cancelled. Cancellation empties the queue, so it arrives here as an ordinary
            // transition to idle and the run still ends.
            if (busy != _runActive)
            {
                _runActive = busy;
                if (busy)
                    OwnsThroughputSnapshot = true;
                _stream.Publish(busy ? new RunStarted() : new RunEnded());
            }

            // Clear the latch once the character queue is empty — the run that produced the
            // escalation has finished (or been cancelled), so the label is no longer current.
            if (Step is null || busy) return;
            Step = null;
            ConfigName = null;
            ItemCount = 0;
            Changed?.Invoke();
        }

        /// <summary>
        /// Release ownership of the throughput snapshot so the status dock can retire the
        /// character-queue progress row once its run has finished. Only meaningful while idle —
        /// an active run keeps re-asserting ownership through <see cref="OnQueueChanged"/>.
        /// </summary>
        public void Dismiss()
        {
            if (!OwnsThroughputSnapshot) return;
            OwnsThroughputSnapshot = false;
            Changed?.Invoke();
        }

        public void Dispose()
        {
            _stream.Event -= OnStreamEvent;
            _queue.Changed -= OnQueueChanged;
        }
    }
}
