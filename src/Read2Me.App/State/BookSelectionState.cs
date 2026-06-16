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
        // paragraph IDs → ancestry; rolled-up node IDs → null
        private readonly Dictionary<Guid, ParagraphSelection?> _selected = new();

        public int SelectedParagraphCount => _selected.Count(kv => kv.Value is not null);

        public bool IsParagraphSelected(Guid paragraphId) =>
            _selected.TryGetValue(paragraphId, out var v) && v is not null;

        public bool IsNodeFullySelected(Guid nodeId) =>
            _selected.TryGetValue(nodeId, out var v) && v is null;

        public TriState NodeState(Guid nodeId)
        {
            if (_selected.TryGetValue(nodeId, out var v) && v is null)
                return TriState.Checked;

            foreach (var sel in _selected.Values)
            {
                if (sel is null) continue;
                if (sel.VolumeId == nodeId || sel.PartId == nodeId || sel.ChapterId == nodeId)
                    return TriState.Indeterminate;
            }

            return TriState.Unchecked;
        }

        public void AddParagraph(Guid id, ParagraphSelection ancestry) =>
            _selected[id] = ancestry;

        public void RemoveParagraph(Guid id) =>
            _selected.Remove(id);

        public void AddNode(Guid id) =>
            _selected[id] = null;

        public void RemoveNode(Guid id) =>
            _selected.Remove(id);

        public void AddParagraphs(IEnumerable<CharacterParagraphRef> refs)
        {
            foreach (var r in refs)
                _selected[r.ParagraphId] = new ParagraphSelection(r.VolumeId, r.PartId, r.ChapterId);
        }

        public void RemoveParagraphs(IEnumerable<Guid> ids)
        {
            foreach (var id in ids)
                _selected.Remove(id);
        }

        public IEnumerable<Guid> FullySelectedVolumeIds() =>
            _selected
                .Where(kv => kv.Value is null && IsVolumeNode(kv.Key))
                .Select(kv => kv.Key);

        public IEnumerable<Guid> FullySelectedPartIds() =>
            _selected
                .Where(kv => kv.Value is null && IsPartNode(kv.Key))
                .Select(kv => kv.Key);

        public IEnumerable<Guid> FullySelectedChapterIds() =>
            _selected
                .Where(kv => kv.Value is null && IsChapterNode(kv.Key))
                .Select(kv => kv.Key);

        // Distinguish node kind by checking ancestry refs in the dict.
        // A rolled-up node id is a volume if any paragraph ancestry has that id as VolumeId.
        private bool IsVolumeNode(Guid id) =>
            _selected.Values.Any(s => s?.VolumeId == id);

        private bool IsPartNode(Guid id) =>
            _selected.Values.Any(s => s?.PartId == id);

        private bool IsChapterNode(Guid id) =>
            _selected.Values.Any(s => s?.ChapterId == id);

        public IEnumerable<Guid> SelectedParagraphIds() =>
            _selected.Where(kv => kv.Value is not null).Select(kv => kv.Key);

        public ParagraphSelection? GetAncestry(Guid paragraphId) =>
            _selected.TryGetValue(paragraphId, out var v) ? v : null;

        public void Clear() => _selected.Clear();

        // Count selected paragraphs whose ancestry matches a given node id.
        public int SelectedCountUnder(Guid nodeId, SelectionNodeKind kind) =>
            _selected.Values.Count(s => s is not null && kind switch
            {
                SelectionNodeKind.Volume => s.VolumeId == nodeId,
                SelectionNodeKind.Part => s.PartId == nodeId,
                SelectionNodeKind.Chapter => s.ChapterId == nodeId,
                _ => false
            });
    }

    public enum SelectionNodeKind { Volume, Part, Chapter }

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
