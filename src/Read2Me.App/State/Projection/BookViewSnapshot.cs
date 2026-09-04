using System;
using System.Collections.Generic;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Audio;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.State.Projection
{
    /// <summary>
    /// Whether the published snapshot still describes the persisted Book.
    /// <see cref="Stale"/> is the Stale Book View projection of ADR 0007 — the last coherent
    /// snapshot kept on screen after reconciliation failed. Only reconciliation can produce it,
    /// so an opened projection is always <see cref="Coherent"/>.
    /// </summary>
    public enum BookViewHealth { Coherent, Stale }

    /// <summary>
    /// Which nodes the reader has open. Intent, not data: a node can be expanded in intent and its
    /// children still absent from <see cref="BookViewBranches"/> for a moment during a rebuild.
    /// </summary>
    public sealed record BookViewExpansion(
        IReadOnlySet<Guid> VolumeIds,
        IReadOnlySet<Guid> PartIds,
        IReadOnlySet<Guid> ChapterIds)
    {
        public static BookViewExpansion Empty { get; } =
            new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

        /// <summary>The open nodes at one level.</summary>
        public IReadOnlySet<Guid> At(BookNodeLevel level) => level switch
        {
            BookNodeLevel.Volume => VolumeIds,
            BookNodeLevel.Part => PartIds,
            _ => ChapterIds,
        };
    }

    /// <summary>
    /// The lazily loaded hierarchy below the overview — only the branches the expansion intent
    /// asked for. A Book is never read whole (ADR 0007), so an absent key means "not loaded",
    /// never "empty".
    /// </summary>
    public sealed record BookViewBranches(
        IReadOnlyDictionary<Guid, IReadOnlyList<Part>> PartsByVolume,
        IReadOnlyDictionary<Guid, IReadOnlyList<Chapter>> ChaptersByPart,
        IReadOnlyDictionary<Guid, IReadOnlyList<Paragraph>> ParagraphsByChapter)
    {
        public static BookViewBranches Empty { get; } = new(
            new Dictionary<Guid, IReadOnlyList<Part>>(),
            new Dictionary<Guid, IReadOnlyList<Chapter>>(),
            new Dictionary<Guid, IReadOnlyList<Paragraph>>());

        public IEnumerable<Paragraph> AllParagraphs()
        {
            foreach (var paragraphs in ParagraphsByChapter.Values)
                foreach (var paragraph in paragraphs)
                    yield return paragraph;
        }
    }

    /// <summary>
    /// Both selections as the snapshot saw them. Whether a selection survives a change is
    /// recomputed by the projection against the new revision — a receipt never carries a selection
    /// verdict (ADR 0007).
    /// </summary>
    public sealed record BookViewSelections(
        IReadOnlySet<Guid> ParagraphIds,
        bool BulkMode,
        IReadOnlySet<Guid> AudioItemIds)
    {
        public static BookViewSelections Empty { get; } =
            new(new HashSet<Guid>(), false, new HashSet<Guid>());
    }

    /// <summary>
    /// One coherent view of a Book at one revision: everything the Book View renders, assembled
    /// from authoritative reads taken together and published atomically. Immutable — a projection
    /// replaces it wholesale rather than patching it, so no reader can ever see a mixture of two
    /// revisions.
    /// </summary>
    public sealed record BookViewSnapshot
    {
        public required ProjectFolderId Folder { get; init; }

        /// <summary>
        /// The project's process-local revision the reads were taken at or after. Snapshots never
        /// move backwards: a receipt older than this is already reflected here.
        /// </summary>
        public required long Revision { get; init; }

        public required BookViewHealth Health { get; init; }

        // ── overview ─────────────────────────────────────────────────────────
        public string? Filename { get; init; }
        public required bool HasContent { get; init; }
        public required IReadOnlyList<Volume> Volumes { get; init; }
        public required int TotalParts { get; init; }
        public required int TotalChapters { get; init; }
        public required bool NarratorOnlyMode { get; init; }
        public required IReadOnlySet<Guid> SelectableNodeIds { get; init; }
        public required IReadOnlyDictionary<Guid, int> NodeCharacterParagraphCounts { get; init; }
        public required IReadOnlyDictionary<Guid, int> AudioNodeCounts { get; init; }

        // ── roster and narration identity ────────────────────────────────────
        public required IReadOnlyList<Character> Characters { get; init; }
        public required NarratorIdentity Narrator { get; init; }

        // ── lazily loaded content and the intent that selected it ────────────
        public required BookViewBranches Branches { get; init; }
        public required BookViewExpansion Expansion { get; init; }

        // ── derived state ────────────────────────────────────────────────────
        public required IReadOnlyList<ParagraphStatusSeedRow> NodeStatus { get; init; }
        public required IReadOnlyDictionary<Guid, AudioReviewInfo> Reviews { get; init; }

        /// <summary>
        /// Item id → the Voice the Audio Queue would actually use, for the items in
        /// <see cref="Branches"/>. Resolved with the rest of the snapshot so a preview can never
        /// name a Voice from an older revision.
        /// </summary>
        public required IReadOnlyDictionary<Guid, string?> VoicePreviews { get; init; }

        // ── transient Book View state ────────────────────────────────────────
        public required BookViewSelections Selections { get; init; }
        public required BookViewMode ViewMode { get; init; }
        public Guid? PlayingAudioItemId { get; init; }

        public string? ResolvedVoiceName(Guid itemId) =>
            VoicePreviews.TryGetValue(itemId, out var name) ? name : null;

        public AudioReviewInfo? ReviewOf(Guid paragraphItemId) =>
            Reviews.TryGetValue(paragraphItemId, out var info) ? info : null;
    }
}
