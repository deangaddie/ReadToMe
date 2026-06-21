using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    public sealed record AudioReviewInfo(
        AudioReviewState State,
        bool NormalizeOk,
        string? NormalizeReason,
        bool VerifyOk,
        double? Wer,
        string? VerifyReason,
        string? Transcript,
        string? OriginalTextSnapshot);

    internal readonly record struct AudioReviewKey(ProjectFolderId Folder, Guid ParagraphItemId);

    /// <summary>
    /// In-memory mirror of the persisted <c>AudioReview</c> rows. A row is present iff an item
    /// needs review; <see cref="Clear"/> removes it when both stages pass.
    /// </summary>
    public sealed class AudioReviewService
    {
        private readonly ConcurrentDictionary<AudioReviewKey, AudioReviewInfo> _reviews = new();

        public event Action? Changed;

        public AudioReviewInfo? ReviewOf(ProjectFolderId folder, Guid paragraphItemId)
            => _reviews.TryGetValue(new AudioReviewKey(folder, paragraphItemId), out var info) ? info : null;

        public void Set(ProjectFolderId folder, Guid paragraphItemId, AudioReviewInfo info)
        {
            _reviews[new AudioReviewKey(folder, paragraphItemId)] = info;
            Changed?.Invoke();
        }

        public void Clear(ProjectFolderId folder, Guid paragraphItemId)
        {
            if (_reviews.TryRemove(new AudioReviewKey(folder, paragraphItemId), out _))
                Changed?.Invoke();
        }

        /// <summary>Replaces all in-memory state for a folder with the supplied rows (sparse).</summary>
        public void Hydrate(ProjectFolderId folder, IEnumerable<(Guid ParagraphItemId, AudioReviewInfo Info)> rows)
        {
            foreach (var key in _reviews.Keys)
                if (key.Folder.Equals(folder))
                    _reviews.TryRemove(key, out _);

            foreach (var (itemId, info) in rows)
                _reviews[new AudioReviewKey(folder, itemId)] = info;

            Changed?.Invoke();
        }
    }
}
