using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Llm;

namespace Read2Me.Tests.Fakes
{
    /// <summary>
    /// A <see cref="VoiceBatchRunner"/> with no scope factory, for tests that never start a batch
    /// through it: they read its state, follow its events or drive a phase by hand.
    /// </summary>
    public static class UnscopedVoiceBatch
    {
        public static VoiceBatchRunner Runner(
            ProjectActivity activity,
            EventBroadcaster<VoiceBatchEvent>? batchEvents = null,
            EventBroadcaster<LlmStreamEvent>? llmEvents = null) =>
            new(
                scopeFactory: null!,
                activity,
                NullLogger<VoiceBatchRunner>.Instance,
                batchEvents ?? new EventBroadcaster<VoiceBatchEvent>(),
                llmEvents ?? new EventBroadcaster<LlmStreamEvent>());
    }
}
