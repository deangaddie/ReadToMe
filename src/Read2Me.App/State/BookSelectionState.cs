using System;
using System.Collections.Generic;
using System.Linq;
using Read2Me.Core.Models;

namespace Read2Me.App.State
{
    public enum TriState { Unchecked, Indeterminate, Checked }

    /// <summary>
    /// An item that can be rolled up under the Volume → Part → Chapter hierarchy.
    /// <see cref="SelectionKey"/> uniquely identifies the item within a folder selection.
    /// </summary>
    public interface IHasNodeAncestry
    {
        Guid VolumeId { get; }
        Guid PartId { get; }
        Guid ChapterId { get; }
        Guid SelectionKey { get; }
    }

    /// <summary>
    /// Shared roll-up selection engine. Holds selected items keyed by <see cref="IHasNodeAncestry.SelectionKey"/>
    /// and derives a node's tri-state on read from the selected count vs. the seeded total
    /// (CONTEXT.md: "Roll-up — a node's tri-state is derived … computed on read").
    /// Always raises <see cref="OnChanged"/> on mutation.
    /// </summary>
    public sealed class RollupSelection<TItem> where TItem : IHasNodeAncestry
    {
        private readonly Dictionary<Guid, TItem> _selected = new();
        private IReadOnlyDictionary<Guid, int> _counts = new Dictionary<Guid, int>();

        public event Action? OnChanged;
        private void NotifyChanged() => OnChanged?.Invoke();

        public void SetCounts(IReadOnlyDictionary<Guid, int> counts)
        {
            _counts = counts;
            NotifyChanged();
        }

        public void Add(TItem item)
        {
            _selected[item.SelectionKey] = item;
            NotifyChanged();
        }

        public void AddRange(IEnumerable<TItem> items)
        {
            foreach (var item in items)
                _selected[item.SelectionKey] = item;
            NotifyChanged();
        }

        public void Remove(Guid key)
        {
            if (_selected.Remove(key))
                NotifyChanged();
        }

        public void RemoveRange(IEnumerable<Guid> keys)
        {
            var changed = false;
            foreach (var key in keys)
            {
                if (_selected.Remove(key))
                    changed = true;
            }
            if (changed) NotifyChanged();
        }

        public void Clear()
        {
            if (_selected.Count > 0)
            {
                _selected.Clear();
                NotifyChanged();
            }
        }

        public bool IsSelected(Guid key) => _selected.ContainsKey(key);
        public IEnumerable<TItem> Selected() => _selected.Values;
        public IEnumerable<Guid> SelectedKeys() => _selected.Keys;
        public bool TryGet(Guid key, out TItem item) => _selected.TryGetValue(key, out item!);
        public int SelectedCount => _selected.Count;

        public int SelectedCountUnder(BookNodeLevel level, Guid nodeId) =>
            _selected.Values.Count(s => level switch
            {
                BookNodeLevel.Volume  => s.VolumeId == nodeId,
                BookNodeLevel.Part    => s.PartId == nodeId,
                _                     => s.ChapterId == nodeId,
            });

        public TriState NodeState(BookNodeLevel level, Guid nodeId)
        {
            var selected = SelectedCountUnder(level, nodeId);
            if (selected == 0) return TriState.Unchecked;
            var total = _counts.TryGetValue(nodeId, out var t) ? t : 0;
            return total > 0 && selected >= total ? TriState.Checked : TriState.Indeterminate;
        }
    }

    public sealed class AudioItemSelection
    {
        private readonly RollupSelection<AudioItemEntry> _inner = new();

        public event Action? OnChanged
        {
            add => _inner.OnChanged += value;
            remove => _inner.OnChanged -= value;
        }

        public void SetCounts(IReadOnlyDictionary<Guid, int> counts) => _inner.SetCounts(counts);

        public void AddItem(AudioItemRef r) => _inner.Add(new AudioItemEntry(r));

        public void RemoveItem(Guid paragraphItemId) => _inner.Remove(paragraphItemId);

        public void AddItems(IEnumerable<AudioItemRef> refs) =>
            _inner.AddRange(refs.Select(r => new AudioItemEntry(r)));

        public void RemoveItems(IEnumerable<Guid> ids) => _inner.RemoveRange(ids);

        public void Clear() => _inner.Clear();

        public bool IsItemSelected(Guid paragraphItemId) => _inner.IsSelected(paragraphItemId);
        public IEnumerable<AudioItemRef> SelectedItems() => _inner.Selected().Select(e => e.Ref);
        public int SelectedItemCount => _inner.SelectedCount;

        public int SelectedCountUnder(BookNodeLevel level, Guid nodeId) =>
            _inner.SelectedCountUnder(level, nodeId);

        public TriState NodeState(BookNodeLevel level, Guid nodeId) =>
            _inner.NodeState(level, nodeId);

        private readonly record struct AudioItemEntry(AudioItemRef Ref) : IHasNodeAncestry
        {
            public Guid VolumeId => Ref.VolumeId;
            public Guid PartId => Ref.PartId;
            public Guid ChapterId => Ref.ChapterId;
            public Guid SelectionKey => Ref.ParagraphItemId;
        }
    }

    public sealed class AudioItemSelectionState
    {
        private readonly Dictionary<string, AudioItemSelection> _folders = new();

        public AudioItemSelection For(ProjectFolderId folderId)
        {
            if (!_folders.TryGetValue(folderId.Value, out var sel))
            {
                sel = new AudioItemSelection();
                _folders[folderId.Value] = sel;
            }
            return sel;
        }

        public void Reset(ProjectFolderId folderId)
        {
            if (_folders.TryGetValue(folderId.Value, out var sel))
                sel.Clear();
        }
    }

    public sealed record ParagraphSelection(Guid VolumeId, Guid PartId, Guid ChapterId);

    public sealed class FolderSelection
    {
        private readonly RollupSelection<ParagraphEntry> _inner = new();

        private bool _bulkMode;

        public FolderSelection()
        {
            // Emptying the selection disarms: a fresh selection always starts in single-assign
            // mode. Hung off the change event rather than derived from the count on read, so
            // "select 12 → arm → uncheck all → check 1" lands disarmed rather than re-armed.
            // Unchecking rows one at a time never calls Clear(), which is why this is not on Clear().
            _inner.OnChanged += () =>
            {
                if (_inner.SelectedCount == 0) _bulkMode = false;
            };
        }

        /// <summary>
        /// Bulk-assign arming: while set, a character picked on a selected row applies across the
        /// whole selection. Read at click time by the presenter — no row renders against it, so the
        /// setter deliberately raises no <see cref="OnChanged"/> (it would repaint the tree for nothing).
        /// </summary>
        public bool BulkMode
        {
            get => _bulkMode;
            set => _bulkMode = value;
        }

        public event Action? OnChanged
        {
            add => _inner.OnChanged += value;
            remove => _inner.OnChanged -= value;
        }

        public void SetCounts(IReadOnlyDictionary<Guid, int> counts) => _inner.SetCounts(counts);

        public void AddParagraph(Guid id, ParagraphSelection ancestry) =>
            _inner.Add(new ParagraphEntry(id, ancestry));

        public void RemoveParagraph(Guid id) => _inner.Remove(id);

        public void AddParagraphs(IEnumerable<CharacterParagraphRef> refs) =>
            _inner.AddRange(refs.Select(r =>
                new ParagraphEntry(r.ParagraphId, new ParagraphSelection(r.VolumeId, r.PartId, r.ChapterId))));

        public void RemoveParagraphs(IEnumerable<Guid> ids) => _inner.RemoveRange(ids);

        public void Clear() => _inner.Clear();

        public bool IsParagraphSelected(Guid paragraphId) => _inner.IsSelected(paragraphId);
        public IEnumerable<Guid> SelectedParagraphIds() => _inner.SelectedKeys();
        public ParagraphSelection? GetAncestry(Guid paragraphId) =>
            _inner.TryGet(paragraphId, out var e) ? e.Ancestry : null;
        public int SelectedParagraphCount => _inner.SelectedCount;

        public int SelectedCountUnder(BookNodeLevel level, Guid nodeId) =>
            _inner.SelectedCountUnder(level, nodeId);

        public TriState NodeState(BookNodeLevel level, Guid nodeId) =>
            _inner.NodeState(level, nodeId);

        public bool IsNodeFullySelected(BookNodeLevel level, Guid nodeId) =>
            NodeState(level, nodeId) == TriState.Checked;

        private readonly record struct ParagraphEntry(Guid Id, ParagraphSelection Ancestry) : IHasNodeAncestry
        {
            public Guid VolumeId => Ancestry.VolumeId;
            public Guid PartId => Ancestry.PartId;
            public Guid ChapterId => Ancestry.ChapterId;
            public Guid SelectionKey => Id;
        }
    }

    public sealed class BookSelectionState
    {
        private readonly Dictionary<string, FolderSelection> _folders = new();

        public FolderSelection For(ProjectFolderId folderId)
        {
            if (!_folders.TryGetValue(folderId.Value, out var sel))
            {
                sel = new FolderSelection();
                _folders[folderId.Value] = sel;
            }
            return sel;
        }

        public void Reset(ProjectFolderId folderId)
        {
            if (_folders.TryGetValue(folderId.Value, out var sel))
                sel.Clear();
        }
    }
}
