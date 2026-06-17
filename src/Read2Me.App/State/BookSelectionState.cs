using System;
using System.Collections.Generic;
using System.Linq;
using Read2Me.Core.Models;

namespace Read2Me.App.State
{
    public enum TriState { Unchecked, Indeterminate, Checked }

    public sealed record ParagraphSelection(Guid VolumeId, Guid PartId, Guid ChapterId);

    public sealed class FolderSelection
    {
        private readonly Dictionary<Guid, ParagraphSelection> _selected = new();
        private IReadOnlyDictionary<Guid, int> _counts = new Dictionary<Guid, int>();

        public void SetCounts(IReadOnlyDictionary<Guid, int> counts) => _counts = counts;

        public void AddParagraph(Guid id, ParagraphSelection ancestry) => _selected[id] = ancestry;
        public void RemoveParagraph(Guid id) => _selected.Remove(id);

        public void AddParagraphs(IEnumerable<CharacterParagraphRef> refs)
        {
            foreach (var r in refs)
                _selected[r.ParagraphId] = new ParagraphSelection(r.VolumeId, r.PartId, r.ChapterId);
        }

        public void RemoveParagraphs(IEnumerable<Guid> ids)
        {
            foreach (var id in ids) _selected.Remove(id);
        }

        public void Clear() => _selected.Clear();

        public bool IsParagraphSelected(Guid paragraphId) => _selected.ContainsKey(paragraphId);
        public IEnumerable<Guid> SelectedParagraphIds() => _selected.Keys;
        public ParagraphSelection? GetAncestry(Guid paragraphId) =>
            _selected.TryGetValue(paragraphId, out var v) ? v : null;
        public int SelectedParagraphCount => _selected.Count;

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

        public bool IsNodeFullySelected(BookNodeLevel level, Guid nodeId) =>
            NodeState(level, nodeId) == TriState.Checked;
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
