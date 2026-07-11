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
    /// </summary>
    public sealed class AttributionProgressState : IDisposable
    {
        private readonly EventBroadcaster<LlmStreamEvent> _stream;
        private readonly CharacterQueueService _queue;

        /// <summary>1-based step index of the latest escalation, or null when none is active.</summary>
        public int? Step { get; private set; }

        /// <summary>Config the latest escalation stepped onto.</summary>
        public string? ConfigName { get; private set; }

        /// <summary>Number of suspect items carried into the latest escalation step.</summary>
        public int ItemCount { get; private set; }

        /// <summary>True while an escalation label should be shown in the progress row.</summary>
        public bool HasEscalation => Step is not null;

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
            if (e is not EscalationStarted es) return;
            Step = es.Step;
            ConfigName = es.ConfigName;
            ItemCount = es.ItemCount;
            Changed?.Invoke();
        }

        // Clear the latch once the character queue is empty — the run that produced the
        // escalation has finished (or been cancelled), so the label is no longer current.
        private void OnQueueChanged()
        {
            if (Step is null) return;
            var snap = _queue.Snapshot();
            if (snap.QueuedCount == 0 && snap.ProcessingCount == 0)
            {
                Step = null;
                ConfigName = null;
                ItemCount = 0;
                Changed?.Invoke();
            }
        }

        public void Dispose()
        {
            _stream.Event -= OnStreamEvent;
            _queue.Changed -= OnQueueChanged;
        }
    }
}
