using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Services;

namespace Read2Me.App.State
{
    public sealed class SelectionCoordinator(IProjectReader reader)
    {
        private IReadOnlyDictionary<Guid, int> _nodeCounts = new Dictionary<Guid, int>();

        public void SetNodeCounts(IReadOnlyDictionary<Guid, int> counts) => _nodeCounts = counts;

        public async Task ToggleParagraphAsync(
            FolderSelection sel, ProjectFolderId folder,
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, bool on)
        {
            if (on)
            {
                sel.AddParagraph(paragraphId, new ParagraphSelection(volumeId, partId, chapterId));
                await WalkUpAsync(sel, folder, chapterId, partId, volumeId);
            }
            else
            {
                sel.RemoveParagraph(paragraphId);
                sel.RemoveNode(chapterId);
                sel.RemoveNode(partId);
                sel.RemoveNode(volumeId);
            }
        }

        public async Task SetNodeAsync(
            FolderSelection sel, ProjectFolderId folder,
            SelectionNodeKind kind, Guid id, bool on, bool unprocessedOnly = false)
        {
            var refs = await GetUnprocessedOrAll(folder, id, kind, unprocessedOnly);

            if (on)
            {
                sel.AddParagraphs(refs);
                sel.AddNode(id);
                MarkDescendantNodesComplete(sel, kind, id, refs);
                await WalkUpFromNodeAsync(sel, folder, kind, id, refs);
            }
            else
            {
                sel.RemoveParagraphs(refs.Select(r => r.ParagraphId));
                sel.RemoveNode(id);
                RemoveDescendantNodes(sel, kind, refs);
                if (refs.Count > 0)
                {
                    var r = refs[0];
                    if (kind == SelectionNodeKind.Chapter)
                    {
                        sel.RemoveNode(r.PartId);
                        sel.RemoveNode(r.VolumeId);
                    }
                    else if (kind == SelectionNodeKind.Part)
                    {
                        sel.RemoveNode(r.VolumeId);
                    }
                }
            }
        }

        private Task<List<CharacterParagraphRef>> GetUnprocessedOrAll(
            ProjectFolderId folder, Guid id, SelectionNodeKind kind, bool unprocessedOnly)
        {
            return kind switch
            {
                SelectionNodeKind.Volume => unprocessedOnly
                    ? reader.GetVolumeUnprocessedCharacterParagraphsAsync(folder, id)
                    : reader.GetVolumeCharacterParagraphsAsync(folder, id),
                SelectionNodeKind.Part => unprocessedOnly
                    ? reader.GetPartUnprocessedCharacterParagraphsAsync(folder, id)
                    : reader.GetPartCharacterParagraphsAsync(folder, id),
                _ => unprocessedOnly
                    ? reader.GetChapterUnprocessedCharacterParagraphsAsync(folder, id)
                    : reader.GetChapterCharacterParagraphsAsync(folder, id),
            };
        }

        private async Task WalkUpAsync(
            FolderSelection sel, ProjectFolderId folder,
            Guid chapterId, Guid partId, Guid volumeId)
        {
            int chCount = await CountForAsync(chapterId, folder, SelectionNodeKind.Chapter);
            var chSelected = sel.SelectedCountUnder(chapterId, SelectionNodeKind.Chapter);
            if (chSelected >= chCount)
            {
                sel.AddNode(chapterId);
                int ptCount = await CountForAsync(partId, folder, SelectionNodeKind.Part);
                var ptSelected = sel.SelectedCountUnder(partId, SelectionNodeKind.Part);
                if (ptSelected >= ptCount)
                {
                    sel.AddNode(partId);
                    int volCount = await CountForAsync(volumeId, folder, SelectionNodeKind.Volume);
                    var volSelected = sel.SelectedCountUnder(volumeId, SelectionNodeKind.Volume);
                    if (volSelected >= volCount)
                        sel.AddNode(volumeId);
                }
            }
        }

        private async Task WalkUpFromNodeAsync(
            FolderSelection sel, ProjectFolderId folder,
            SelectionNodeKind kind, Guid id, List<CharacterParagraphRef> refs)
        {
            if (refs.Count == 0) return;

            if (kind == SelectionNodeKind.Chapter)
            {
                var r = refs[0];
                await WalkUpAsync(sel, folder, id, r.PartId, r.VolumeId);
            }
            else if (kind == SelectionNodeKind.Part)
            {
                var r = refs[0];
                int volCount = await CountForAsync(r.VolumeId, folder, SelectionNodeKind.Volume);
                var volSelected = sel.SelectedCountUnder(r.VolumeId, SelectionNodeKind.Volume);
                if (volSelected >= volCount)
                    sel.AddNode(r.VolumeId);
            }
        }

        // Returns count from in-memory map; falls back to reader if not yet loaded
        // (e.g. before first LoadAsync or after a split/merge reload race).
        private ValueTask<int> CountForAsync(Guid nodeId, ProjectFolderId folder, SelectionNodeKind kind)
        {
            if (_nodeCounts.TryGetValue(nodeId, out var c)) return ValueTask.FromResult(c);
            return new ValueTask<int>(kind switch
            {
                SelectionNodeKind.Chapter => reader.GetChapterCharacterParagraphCountAsync(folder, nodeId),
                SelectionNodeKind.Part    => reader.GetPartCharacterParagraphCountAsync(folder, nodeId),
                _                         => reader.GetVolumeCharacterParagraphCountAsync(folder, nodeId),
            });
        }

        private static void MarkDescendantNodesComplete(
            FolderSelection sel, SelectionNodeKind kind, Guid id, List<CharacterParagraphRef> refs)
        {
            if (kind == SelectionNodeKind.Volume)
            {
                foreach (var partId in refs.Select(r => r.PartId).Distinct())
                    sel.AddNode(partId);
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    sel.AddNode(chapterId);
            }
            else if (kind == SelectionNodeKind.Part)
            {
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    sel.AddNode(chapterId);
            }
        }

        private static void RemoveDescendantNodes(
            FolderSelection sel, SelectionNodeKind kind, List<CharacterParagraphRef> refs)
        {
            if (kind == SelectionNodeKind.Volume)
            {
                foreach (var partId in refs.Select(r => r.PartId).Distinct())
                    sel.RemoveNode(partId);
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    sel.RemoveNode(chapterId);
            }
            else if (kind == SelectionNodeKind.Part)
            {
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    sel.RemoveNode(chapterId);
            }
        }
    }
}
