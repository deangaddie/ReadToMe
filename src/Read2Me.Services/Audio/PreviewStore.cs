using System.Collections.Concurrent;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// A rendered voice-editor preview: every step's outcome and audio, remembered under a minted
    /// id so that Apply can name what the user heard instead of re-sending bytes or re-rendering.
    /// </summary>
    public sealed record PreviewEntry(
        string PreviewId, ProjectFolderId Folder, Guid VoiceId, IReadOnlyList<ChainStepOutcome> Stages, DateTimeOffset ExpiresAt)
    {
        /// <summary>The last stage's audio — what Apply writes over the live voice WAV.</summary>
        public byte[] Final => Stages[^1].Audio;
    }

    public interface IPreviewStore
    {
        PreviewEntry Save(ProjectFolderId folder, Guid voiceId, IReadOnlyList<ChainStepOutcome> stages);

        /// <summary>The entry for <paramref name="previewId"/>, or null when unknown or expired.</summary>
        PreviewEntry? TryGet(string previewId);
    }

    /// <summary>
    /// In-memory, process-wide (not circuit-bound: the HTTP API has no circuit). Entries expire after
    /// <see cref="DefaultTtl"/>; expired ones are dropped on the next save or lookup, so an idle
    /// store holds at most the last half hour of previews — voice WAVs are seconds long, so that is
    /// a few megabytes, not a cache to manage.
    /// </summary>
    public sealed class PreviewStore(TimeProvider clock, TimeSpan? ttl = null) : IPreviewStore
    {
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

        private readonly ConcurrentDictionary<string, PreviewEntry> _entries = new();
        private readonly TimeSpan _ttl = ttl ?? DefaultTtl;

        public PreviewEntry Save(ProjectFolderId folder, Guid voiceId, IReadOnlyList<ChainStepOutcome> stages)
        {
            if (stages.Count == 0)
                throw new ArgumentException("A preview needs at least one stage.", nameof(stages));

            Sweep();
            var entry = new PreviewEntry(Guid.NewGuid().ToString("N"), folder, voiceId, stages, clock.GetUtcNow() + _ttl);
            _entries[entry.PreviewId] = entry;
            return entry;
        }

        public PreviewEntry? TryGet(string previewId)
        {
            if (!_entries.TryGetValue(previewId, out var entry))
                return null;

            if (entry.ExpiresAt > clock.GetUtcNow())
                return entry;

            _entries.TryRemove(previewId, out _);
            return null;
        }

        private void Sweep()
        {
            var now = clock.GetUtcNow();
            foreach (var (id, entry) in _entries)
                if (entry.ExpiresAt <= now)
                    _entries.TryRemove(id, out _);
        }
    }
}
