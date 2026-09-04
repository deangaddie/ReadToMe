using System;
using System.Collections.Generic;
using Read2Me.Core.Models;

namespace Read2Me.App.State
{
    /// <summary>
    /// Where each project's expansion intent lives — which volumes, parts and chapters the reader
    /// has open. Intent only: the content behind an open node is read by
    /// <see cref="Projection.BookViewProjection"/> and published on its snapshot, so this holds no
    /// Book data and never goes stale against one (ADR 0007).
    /// </summary>
    public class BookTreeState
    {
        private readonly Dictionary<ProjectFolderId, FolderExpansion> _states = new();

        public FolderExpansion For(ProjectFolderId folderId)
        {
            if (!_states.TryGetValue(folderId, out var state))
                _states[folderId] = state = new FolderExpansion();
            return state;
        }
    }

    /// <summary>
    /// One project's open nodes. Written by <see cref="Projection.BookViewProjection"/> — through an
    /// expansion intent, or by pruning ids the Book no longer contains as it rebuilds — plus the two
    /// structural fix-ups a split and a merge need to keep the reader's place.
    /// </summary>
    public class FolderExpansion
    {
        private readonly Dictionary<BookNodeLevel, HashSet<Guid>> _open = new()
        {
            [BookNodeLevel.Volume] = [],
            [BookNodeLevel.Part] = [],
            [BookNodeLevel.Chapter] = [],
        };

        /// <summary>The open nodes at one level, to read or to write.</summary>
        public HashSet<Guid> At(BookNodeLevel level) => _open[level];

        public HashSet<Guid> ExpandedVolumeIds => _open[BookNodeLevel.Volume];
        public HashSet<Guid> ExpandedPartIds => _open[BookNodeLevel.Part];
        public HashSet<Guid> ExpandedChapterIds => _open[BookNodeLevel.Chapter];

        /// <summary>Transfer expansion from the deleted node to the survivor after a merge.</summary>
        public void FixMergeExpansion(Guid survivorId, Guid deletedId)
        {
            foreach (var ids in _open.Values)
                if (ids.Remove(deletedId)) ids.Add(survivorId);
        }

        /// <summary>
        /// After a split: if the source node was open, open the newly created sibling too, so both
        /// halves of what the reader was looking at stay in view.
        /// <para>
        /// Level-agnostic, like its merge sibling. A node id is unique across the hierarchy, so
        /// "wherever the source was open" is enough — and a writer reporting a split has no business
        /// knowing which level of a view its two nodes sit at.
        /// </para>
        /// </summary>
        public void CarrySplitExpansion(Guid sourceId, Guid newId)
        {
            foreach (var ids in _open.Values)
                if (ids.Contains(sourceId)) ids.Add(newId);
        }
    }
}
