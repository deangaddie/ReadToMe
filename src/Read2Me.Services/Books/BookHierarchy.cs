using System;
using System.Collections.Generic;
using System.Linq;
using FractionalIndexing;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Books
{
    /// <summary>
    /// Pure in-memory representation of a book hierarchy.
    /// Plan* methods encode mutation logic without touching the DB.
    /// </summary>
    public class BookHierarchy
    {
        public List<Volume> Volumes { get; init; } = [];
        /// <summary>Parts keyed by VolumeId.</summary>
        public Dictionary<Guid, List<Part>> Parts { get; init; } = [];
        /// <summary>Chapters keyed by PartId.</summary>
        public Dictionary<Guid, List<Chapter>> Chapters { get; init; } = [];
        /// <summary>Paragraphs keyed by ChapterId.</summary>
        public Dictionary<Guid, List<Paragraph>> Paragraphs { get; init; } = [];
        /// <summary>Items keyed by ParagraphId.</summary>
        public Dictionary<Guid, List<ParagraphItem>> Items { get; init; } = [];

        // ---------------------------------------------------------------
        // PlanSplitVolume: split at a Part boundary, creating a new Volume.
        // Part at partId and all subsequent siblings move to new Volume.
        // ---------------------------------------------------------------
        public HierarchyMutation? PlanSplitVolume(Guid partId, string? newVolumeTitle)
        {
            var (volumeId, siblings) = FindParentAndSiblings(Parts, partId, p => p.Id);
            if (volumeId == null) return null;

            var splitIdx = siblings.FindIndex(p => p.Id == partId);
            if (splitIdx < 0) return null;

            var currentVolIdx = Volumes.FindIndex(v => v.Id == volumeId.Value);
            if (currentVolIdx < 0) return null;

            var currentVol = Volumes[currentVolIdx];
            var nextOrder = currentVolIdx < Volumes.Count - 1 ? Volumes[currentVolIdx + 1].Order : null;

            var newVolume = new Volume
            {
                Id = Guid.NewGuid(),
                Title = newVolumeTitle ?? currentVol.Title,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentVol.Order, nextOrder),
            };

            var movedParts = siblings.Skip(splitIdx).ToList();
            foreach (var p in movedParts) p.VolumeId = newVolume.Id;

            return new HierarchyMutation(
                ToAdd: [newVolume],
                ToDelete: [],
                ToUpdate: movedParts.Cast<object>().ToList());
        }

        // ---------------------------------------------------------------
        // PlanSplitPart: split at a Chapter boundary, creating a new Part.
        // ---------------------------------------------------------------
        public HierarchyMutation? PlanSplitPart(Guid chapterId, string? newPartTitle)
        {
            var (partId, chapterSiblings) = FindParentAndSiblings(Chapters, chapterId, c => c.Id);
            if (partId == null) return null;

            var splitIdx = chapterSiblings.FindIndex(c => c.Id == chapterId);
            if (splitIdx < 0) return null;

            var (_, partSiblings) = FindParentAndSiblings(Parts, partId.Value, p => p.Id);
            var partIdx = partSiblings.FindIndex(p => p.Id == partId.Value);
            if (partIdx < 0) return null;

            var currentPart = partSiblings[partIdx];
            var nextOrder = partIdx < partSiblings.Count - 1 ? partSiblings[partIdx + 1].Order : null;

            var newPart = new Part
            {
                Id = Guid.NewGuid(),
                VolumeId = currentPart.VolumeId,
                Title = newPartTitle ?? currentPart.Title,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentPart.Order, nextOrder),
            };

            var movedChapters = chapterSiblings.Skip(splitIdx).ToList();
            foreach (var c in movedChapters) c.PartId = newPart.Id;

            return new HierarchyMutation(
                ToAdd: [newPart],
                ToDelete: [],
                ToUpdate: movedChapters.Cast<object>().ToList());
        }

        // ---------------------------------------------------------------
        // PlanSplitChapter: split at a Paragraph boundary, creating a new Chapter.
        // ---------------------------------------------------------------
        public HierarchyMutation? PlanSplitChapter(Guid paragraphId, string? newChapterTitle)
        {
            var (chapterId, paragraphSiblings) = FindParentAndSiblings(Paragraphs, paragraphId, p => p.Id);
            if (chapterId == null) return null;

            var splitIdx = paragraphSiblings.FindIndex(p => p.Id == paragraphId);
            if (splitIdx < 0) return null;

            var (_, chapterSiblings) = FindParentAndSiblings(Chapters, chapterId.Value, c => c.Id);
            var chapterIdx = chapterSiblings.FindIndex(c => c.Id == chapterId.Value);
            if (chapterIdx < 0) return null;

            var currentChapter = chapterSiblings[chapterIdx];
            var nextOrder = chapterIdx < chapterSiblings.Count - 1 ? chapterSiblings[chapterIdx + 1].Order : null;

            var newChapter = new Chapter
            {
                Id = Guid.NewGuid(),
                PartId = currentChapter.PartId,
                Title = newChapterTitle ?? currentChapter.Title,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentChapter.Order, nextOrder),
            };

            var movedParagraphs = paragraphSiblings.Skip(splitIdx).ToList();
            foreach (var p in movedParagraphs) p.ChapterId = newChapter.Id;

            return new HierarchyMutation(
                ToAdd: [newChapter],
                ToDelete: [],
                ToUpdate: movedParagraphs.Cast<object>().ToList());
        }

        // ---------------------------------------------------------------
        // PlanSplitParagraph: split at an Item boundary, creating a new Paragraph.
        // ---------------------------------------------------------------
        public HierarchyMutation? PlanSplitParagraph(Guid itemId)
        {
            var (paragraphId, itemSiblings) = FindParentAndSiblings(Items, itemId, i => i.Id);
            if (paragraphId == null) return null;

            var splitIdx = itemSiblings.FindIndex(i => i.Id == itemId);
            if (splitIdx < 0) return null;

            var (_, paragraphSiblings) = FindParentAndSiblings(Paragraphs, paragraphId.Value, p => p.Id);
            var paragraphIdx = paragraphSiblings.FindIndex(p => p.Id == paragraphId.Value);
            if (paragraphIdx < 0) return null;

            var currentParagraph = paragraphSiblings[paragraphIdx];
            var nextOrder = paragraphIdx < paragraphSiblings.Count - 1 ? paragraphSiblings[paragraphIdx + 1].Order : null;

            var newParagraph = new Paragraph
            {
                Id = Guid.NewGuid(),
                ChapterId = currentParagraph.ChapterId,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentParagraph.Order, nextOrder),
            };

            var movedItems = itemSiblings.Skip(splitIdx).ToList();
            foreach (var i in movedItems) i.ParagraphId = newParagraph.Id;

            return new HierarchyMutation(
                ToAdd: [newParagraph],
                ToDelete: [],
                ToUpdate: movedItems.Cast<object>().ToList());
        }

        // ---------------------------------------------------------------
        // PlanMerge* — pure merge planning, no DB access.
        // Returns null when the operation is a no-op.
        // ---------------------------------------------------------------

        public HierarchyMutation? PlanMergeVolume(Guid volumeId, MergeDirection dir)
        {
            var idx = Volumes.FindIndex(v => v.Id == volumeId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(Volumes, idx - 1, idx, v => v.Id,
                    id => Parts.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((Part)child).VolumeId = winnerId)
                : MergeSiblings(Volumes, idx, idx + 1, v => v.Id,
                    id => Parts.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((Part)child).VolumeId = winnerId);
        }

        public HierarchyMutation? PlanMergePart(Guid partId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Parts, partId, p => p.Id);
            var idx = siblings.FindIndex(p => p.Id == partId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(siblings, idx - 1, idx, p => p.Id,
                    id => Chapters.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((Chapter)child).PartId = winnerId)
                : MergeSiblings(siblings, idx, idx + 1, p => p.Id,
                    id => Chapters.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((Chapter)child).PartId = winnerId);
        }

        public HierarchyMutation? PlanMergeChapter(Guid chapterId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Chapters, chapterId, c => c.Id);
            var idx = siblings.FindIndex(c => c.Id == chapterId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(siblings, idx - 1, idx, c => c.Id,
                    id => Paragraphs.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((Paragraph)child).ChapterId = winnerId)
                : MergeSiblings(siblings, idx, idx + 1, c => c.Id,
                    id => Paragraphs.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((Paragraph)child).ChapterId = winnerId);
        }

        public HierarchyMutation? PlanMergeParagraph(Guid paragraphId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Paragraphs, paragraphId, p => p.Id);
            var idx = siblings.FindIndex(p => p.Id == paragraphId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(siblings, idx - 1, idx, p => p.Id,
                    id => Items.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((ParagraphItem)child).ParagraphId = winnerId)
                : MergeSiblings(siblings, idx, idx + 1, p => p.Id,
                    id => Items.TryGetValue(id, out var ch) ? ch.Cast<object>().ToList() : [],
                    (child, winnerId) => ((ParagraphItem)child).ParagraphId = winnerId);
        }

        public HierarchyMutation? PlanMergeParagraphItem(Guid itemId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Items, itemId, i => i.Id);
            var idx = siblings.FindIndex(i => i.Id == itemId);
            if (idx < 0) return null;

            int winnerIdx, loserIdx;
            if (dir == MergeDirection.Previous)
            {
                winnerIdx = idx - 1;
                loserIdx = idx;
            }
            else
            {
                winnerIdx = idx;
                loserIdx = idx + 1;
            }

            if (winnerIdx < 0 || loserIdx >= siblings.Count) return null;

            var winner = siblings[winnerIdx];
            var loser = siblings[loserIdx];

            winner.Text = string.IsNullOrWhiteSpace(winner.Text)
                ? loser.Text
                : string.IsNullOrWhiteSpace(loser.Text) ? winner.Text : winner.Text + " " + loser.Text;

            return new HierarchyMutation(ToAdd: [], ToDelete: [loser], ToUpdate: [winner]);
        }

        private static HierarchyMutation? MergeSiblings<TEntity>(
            List<TEntity> siblings,
            int winnerIdx,
            int loserIdx,
            Func<TEntity, Guid> getId,
            Func<Guid, List<object>> getChildren,
            Action<object, Guid> reassign)
        {
            if (winnerIdx < 0 || loserIdx >= siblings.Count) return null;
            var winner = siblings[winnerIdx];
            var loser = siblings[loserIdx];
            var children = getChildren(getId(loser));
            foreach (var child in children) reassign(child, getId(winner));
            return new HierarchyMutation(ToAdd: [], ToDelete: [loser!], ToUpdate: children);
        }

        // ---------------------------------------------------------------
        // PlanFrontMatterInsert — pure planning for AddBookTitle.
        // Returns the structural HierarchyMutation (new Volume/Part/Chapter if needed)
        // and the target chapter id + the order key of the first existing paragraph
        // (null if chapter is empty) so TitleInserter can place paragraphs.
        // Returns null if there are no volumes.
        // ---------------------------------------------------------------

        public (HierarchyMutation Mutation, Guid ChapterId, string? FirstParagraphOrder)? PlanFrontMatterInsert()
        {
            if (Volumes.Count == 0) return null;

            Guid chapterId;
            var toAdd = new List<object>();

            if (Volumes.Count > 1)
            {
                var firstVol = Volumes[0];
                var newVol = new Volume
                {
                    Id = Guid.NewGuid(),
                    Title = string.Empty,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, firstVol.Order),
                };
                var newPart = new Part
                {
                    Id = Guid.NewGuid(),
                    VolumeId = newVol.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                };
                var newChapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = newPart.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                };
                toAdd.AddRange([newVol, newPart, newChapter]);
                chapterId = newChapter.Id;
            }
            else
            {
                var vol = Volumes[0];
                var parts = Parts.TryGetValue(vol.Id, out var ps) ? ps : [];

                if (parts.Count > 1)
                {
                    var firstPart = parts[0];
                    var newPart = new Part
                    {
                        Id = Guid.NewGuid(),
                        VolumeId = vol.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, firstPart.Order),
                    };
                    var newChapter = new Chapter
                    {
                        Id = Guid.NewGuid(),
                        PartId = newPart.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                    };
                    toAdd.AddRange([newPart, newChapter]);
                    chapterId = newChapter.Id;
                }
                else
                {
                    var part = parts.Count > 0 ? parts[0] : null;
                    if (part == null) return null;

                    var chapters = Chapters.TryGetValue(part.Id, out var cs) ? cs : [];
                    var firstChapter = chapters.Count > 0 ? chapters[0] : null;

                    var newChapter = new Chapter
                    {
                        Id = Guid.NewGuid(),
                        PartId = part.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, firstChapter?.Order),
                    };
                    toAdd.Add(newChapter);
                    chapterId = newChapter.Id;
                }
            }

            var mutation = new HierarchyMutation(ToAdd: toAdd, ToDelete: [], ToUpdate: []);
            return (mutation, chapterId, null);
        }

        // ---------------------------------------------------------------
        // PlanTitleChapters — per titled Volume/Part, plan a new Chapter
        // inserted before the first existing chapter in the first part.
        // Returns one entry per node that has a non-blank title.
        // ---------------------------------------------------------------

        public List<(Guid NodeId, string Title, Chapter NewChapter, string? FirstChapterOrder)> PlanVolumeTitleChapters()
        {
            var results = new List<(Guid, string, Chapter, string?)>();
            foreach (var vol in Volumes)
            {
                if (string.IsNullOrWhiteSpace(vol.Title)) continue;
                var parts = Parts.TryGetValue(vol.Id, out var ps) ? ps : [];
                var firstPart = parts.Count > 0 ? parts[0] : null;
                if (firstPart == null) continue;
                var chapters = Chapters.TryGetValue(firstPart.Id, out var cs) ? cs : [];
                var firstChapter = chapters.Count > 0 ? chapters[0] : null;
                var newChapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = firstPart.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, firstChapter?.Order),
                };
                results.Add((vol.Id, vol.Title, newChapter, firstChapter?.Order));
            }
            return results;
        }

        public List<(Guid NodeId, string Title, Chapter NewChapter, string? FirstChapterOrder)> PlanPartTitleChapters()
        {
            var results = new List<(Guid, string, Chapter, string?)>();
            foreach (var partList in Parts.Values)
            {
                foreach (var part in partList)
                {
                    if (string.IsNullOrWhiteSpace(part.Title)) continue;
                    var chapters = Chapters.TryGetValue(part.Id, out var cs) ? cs : [];
                    var firstChapter = chapters.Count > 0 ? chapters[0] : null;
                    var newChapter = new Chapter
                    {
                        Id = Guid.NewGuid(),
                        PartId = part.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, firstChapter?.Order),
                    };
                    results.Add((part.Id, part.Title, newChapter, firstChapter?.Order));
                }
            }
            return results;
        }

        public List<(Guid ChapterId, string Title, string? FirstParagraphOrder)> PlanChapterTitleInsertions()
        {
            var results = new List<(Guid, string, string?)>();
            foreach (var chapterList in Chapters.Values)
            {
                foreach (var ch in chapterList)
                {
                    if (string.IsNullOrWhiteSpace(ch.Title)) continue;
                    var paragraphs = Paragraphs.TryGetValue(ch.Id, out var ps) ? ps : [];
                    var firstParagraph = paragraphs.Count > 0 ? paragraphs[0] : null;
                    results.Add((ch.Id, ch.Title, firstParagraph?.Order));
                }
            }
            return results;
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private static (Guid? parentId, List<TChild> siblings) FindParentAndSiblings<TChild>(
            Dictionary<Guid, List<TChild>> byParent,
            Guid childId,
            Func<TChild, Guid> getId)
        {
            foreach (var (parentId, children) in byParent)
            {
                if (children.Any(c => getId(c) == childId))
                    return (parentId, children);
            }
            return (null, []);
        }
    }

    public record HierarchyMutation(
        IReadOnlyList<object> ToAdd,
        IReadOnlyList<object> ToDelete,
        IReadOnlyList<object> ToUpdate);
}
