using System;
using System.Collections.Generic;
using System.Linq;
using FractionalIndexing;
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
