using Read2Me.Core.Utils;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

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
                Order = OrderHelper.GetBetween(currentVol.Order, nextOrder),
            };

            var movedParts = siblings.Skip(splitIdx).ToList();
            foreach (var p in movedParts) p.VolumeId = newVolume.Id;

            return new HierarchyMutation(
                ToAdd: [newVolume],
                ToDelete: [],
                ToUpdate: movedParts.Cast<IBookEntity>().ToList());
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
                Order = OrderHelper.GetBetween(currentPart.Order, nextOrder),
            };

            var movedChapters = chapterSiblings.Skip(splitIdx).ToList();
            foreach (var c in movedChapters) c.PartId = newPart.Id;

            return new HierarchyMutation(
                ToAdd: [newPart],
                ToDelete: [],
                ToUpdate: movedChapters.Cast<IBookEntity>().ToList());
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
                Order = OrderHelper.GetBetween(currentChapter.Order, nextOrder),
            };

            var movedParagraphs = paragraphSiblings.Skip(splitIdx).ToList();
            foreach (var p in movedParagraphs) p.ChapterId = newChapter.Id;

            return new HierarchyMutation(
                ToAdd: [newChapter],
                ToDelete: [],
                ToUpdate: movedParagraphs.Cast<IBookEntity>().ToList());
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
                Order = OrderHelper.GetBetween(currentParagraph.Order, nextOrder),
            };

            var movedItems = itemSiblings.Skip(splitIdx).ToList();
            foreach (var i in movedItems) i.ParagraphId = newParagraph.Id;

            return new HierarchyMutation(
                ToAdd: [newParagraph],
                ToDelete: [],
                ToUpdate: movedItems.Cast<IBookEntity>().ToList());
        }

        // ---------------------------------------------------------------
        // PlanInsertParagraphItem: create one Speech item beside an anchor item,
        // inside the anchor's own Paragraph. Insertion never crosses a Paragraph
        // boundary — "Before" on the first item lands first in that same Paragraph.
        //
        // The new item is born unattributed: no CharacterId, no VoiceInstructions,
        // no AudioFileName. The anchor held two speakers by construction, so
        // inheriting its speaker would stamp a confident wrong answer that looks
        // attributed and never reaches the attribution queue (spec D5).
        //
        // Returns null when the anchor is unknown, is a pause, or the text is blank.
        // ---------------------------------------------------------------
        public HierarchyMutation? PlanInsertParagraphItem(Guid anchorItemId, InsertPosition position, string text)
        {
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;

            var (paragraphId, siblings) = FindParentAndSiblings(Items, anchorItemId, i => i.Id);
            if (paragraphId == null) return null;

            var idx = siblings.FindIndex(i => i.Id == anchorItemId);
            if (idx < 0) return null;

            var anchor = siblings[idx];
            // A Speech item inside a pause paragraph is a structure the readers assume cannot
            // exist, so the refusal lives here rather than only in the menu (spec D7).
            if (ParagraphItemKinds.IsPause(anchor.ItemType)) return null;

            string? prevOrder, nextOrder;
            if (position == InsertPosition.Before)
            {
                prevOrder = idx > 0 ? siblings[idx - 1].Order : null;
                nextOrder = anchor.Order;
            }
            else
            {
                prevOrder = anchor.Order;
                nextOrder = idx < siblings.Count - 1 ? siblings[idx + 1].Order : null;
            }

            var newItem = UnattributedSpeech(paragraphId.Value, OrderHelper.GetBetween(prevOrder, nextOrder), trimmed);
            return new HierarchyMutation(ToAdd: [newItem], ToDelete: [], ToUpdate: []);
        }

        // ---------------------------------------------------------------
        // InsertedKind: the kind of node an insert creates. Before/After make a
        // sibling at the anchor's own level, AtStart/AtEnd a child one level down.
        // Null for an item anchor: Before/After an item is the item insert, and
        // an item has no children.
        // ---------------------------------------------------------------
        public static NodeKind? InsertedKind(NodeKind anchorKind, Placement placement)
        {
            if (anchorKind == NodeKind.ParagraphItem) return null;
            var sibling = placement is Placement.Before or Placement.After;
            return anchorKind switch
            {
                NodeKind.Volume => sibling ? NodeKind.Volume : NodeKind.Part,
                NodeKind.Part => sibling ? NodeKind.Part : NodeKind.Chapter,
                NodeKind.Chapter => sibling ? NodeKind.Chapter : NodeKind.Paragraph,
                _ => sibling ? NodeKind.Paragraph : NodeKind.ParagraphItem,
            };
        }

        // ---------------------------------------------------------------
        // PlanInsertNode: create one node beside or inside the anchor. Only new
        // rows are added — the order key goes between the neighbours, so no
        // existing node is reordered, reparented or removed.
        //
        // A new Volume, Part or Chapter is born empty, its title optional: a
        // blank one leaves it untitled. A new Paragraph is born holding its
        // first Speech item, unattributed: an empty Paragraph reads as a pause,
        // and a new item has no speaker yet. A Speech item at a paragraph's
        // start or end is born the same way, first or last inside it; a pause
        // paragraph takes none.
        //
        // Returns null for a combination it does not plan, an unknown anchor,
        // a pause paragraph's start or end, or blank paragraph or item text.
        // ---------------------------------------------------------------
        public HierarchyMutation? PlanInsertNode(
            NodeKind anchorKind, Guid anchorId, Placement placement, string? title, string? text)
        {
            var beside = placement is Placement.Before or Placement.After;
            (Guid ParentId, string? Prev, string? Next)? slot = (anchorKind, beside) switch
            {
                (NodeKind.Volume, true) => VolumeSlot(anchorId, placement),
                (NodeKind.Volume, false) => SlotInside(Volumes.Any(v => v.Id == anchorId), anchorId, Parts, p => p.Order, placement),
                (NodeKind.Part, true) => SlotBeside(Parts, anchorId, p => p.Id, p => p.Order, placement),
                (NodeKind.Part, false) => SlotInside(Holds(Parts, anchorId, p => p.Id), anchorId, Chapters, c => c.Order, placement),
                (NodeKind.Chapter, true) => SlotBeside(Chapters, anchorId, c => c.Id, c => c.Order, placement),
                (NodeKind.Chapter, false) => SlotInside(Holds(Chapters, anchorId, c => c.Id), anchorId, Paragraphs, p => p.Order, placement),
                (NodeKind.Paragraph, true) => SlotBeside(Paragraphs, anchorId, p => p.Id, p => p.Order, placement),
                (NodeKind.Paragraph, false) => SlotInside(TakesSpeechAtEnds(anchorId), anchorId, Items, i => i.Order, placement),
                _ => null,
            };
            if (slot is not var (parentId, prev, next)) return null;

            var order = OrderHelper.GetBetween(prev, next);
            var newTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            var trimmed = text?.Trim();
            IBookEntity[]? added = InsertedKind(anchorKind, placement) switch
            {
                NodeKind.Volume => [new Volume { Id = Guid.NewGuid(), Title = newTitle ?? string.Empty, Order = order }],
                NodeKind.Part => [new Part { Id = Guid.NewGuid(), VolumeId = parentId, Title = newTitle, Order = order }],
                NodeKind.Chapter => [new Chapter { Id = Guid.NewGuid(), PartId = parentId, Title = newTitle, Order = order }],
                _ when string.IsNullOrEmpty(trimmed) => null,
                NodeKind.Paragraph => NewParagraph(parentId, order, trimmed),
                // The Item insertion rules, anchored on the paragraph rather than an item, so a
                // leading or trailing pause item does not block it.
                NodeKind.ParagraphItem => [UnattributedSpeech(parentId, order, trimmed)],
                _ => throw new InvalidOperationException($"No slot is planned for {anchorKind} {placement}."),
            };
            return added is null ? null : new HierarchyMutation(ToAdd: [.. added], ToDelete: [], ToUpdate: []);
        }

        private static IBookEntity[] NewParagraph(Guid chapterId, string order, string text)
        {
            var paragraph = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapterId, Order = order };
            return [paragraph, UnattributedSpeech(paragraph.Id, OrderHelper.GetBetween(null, null), text)];
        }

        // A new Speech item with no speaker, delivery cue or audio, so it reaches the
        // attribution queue rather than looking attributed (Item insertion).
        private static ParagraphItem UnattributedSpeech(Guid paragraphId, string order, string text) => new()
        {
            Id = Guid.NewGuid(),
            ParagraphId = paragraphId,
            ItemType = ParagraphItemType.Speech,
            Text = text,
            CharacterId = null,
            VoiceInstructions = null,
            AudioFileName = null,
            Order = order,
        };

        // Insert slots: the parent a new node goes under and the order keys it
        // goes between. Null when the anchor is not where its kind says it is.

        // Volumes have no parent row; Guid.Empty stands in and is never read.
        private (Guid, string?, string?)? VolumeSlot(Guid anchorId, Placement placement)
        {
            var idx = Volumes.FindIndex(v => v.Id == anchorId);
            if (idx < 0) return null;
            var (prev, next) = Beside(Volumes, idx, v => v.Order, placement);
            return (Guid.Empty, prev, next);
        }

        private static (Guid, string?, string?)? SlotBeside<T>(
            Dictionary<Guid, List<T>> byParent, Guid anchorId, Func<T, Guid> getId, Func<T, string> getOrder, Placement placement)
        {
            var (parentId, siblings) = FindParentAndSiblings(byParent, anchorId, getId);
            if (parentId == null) return null;
            var (prev, next) = Beside(siblings, siblings.FindIndex(s => getId(s) == anchorId), getOrder, placement);
            return (parentId.Value, prev, next);
        }

        private static (Guid, string?, string?)? SlotInside<T>(
            bool anchorExists, Guid anchorId, Dictionary<Guid, List<T>> childrenByParent, Func<T, string> getOrder, Placement placement)
        {
            if (!anchorExists) return null;
            if (!childrenByParent.TryGetValue(anchorId, out var children) || children.Count == 0)
                return (anchorId, null, null);
            return placement == Placement.AtStart
                ? (anchorId, null, getOrder(children[0]))
                : (anchorId, getOrder(children[^1]), null);
        }

        private static (string?, string?) Beside<T>(List<T> siblings, int idx, Func<T, string> getOrder, Placement placement) =>
            placement == Placement.Before
                ? (idx > 0 ? getOrder(siblings[idx - 1]) : null, getOrder(siblings[idx]))
                : (getOrder(siblings[idx]), idx < siblings.Count - 1 ? getOrder(siblings[idx + 1]) : null);

        // A paragraph takes a speech item at its start or end unless it is a pause paragraph:
        // Speech inside one is a structure the readers assume cannot exist.
        private bool TakesSpeechAtEnds(Guid paragraphId)
        {
            var (_, siblings) = FindParentAndSiblings(Paragraphs, paragraphId, p => p.Id);
            var paragraph = siblings.Find(p => p.Id == paragraphId);
            return paragraph is not null && !IsPauseParagraph(paragraph);
        }

        private static bool Holds<T>(Dictionary<Guid, List<T>> byParent, Guid childId, Func<T, Guid> getId) =>
            FindParentAndSiblings(byParent, childId, getId).parentId != null;

        // ---------------------------------------------------------------
        // PlanMerge* — pure merge planning, no DB access.
        // Returns null when the operation is a no-op.
        //
        // A plan names the survivor as well as the writes, because a merge is the one structural
        // change whose two nodes a reader has to relate: whatever was open on the node that goes
        // away belongs on the one that took its place (ADR 0007).
        // ---------------------------------------------------------------

        public BookMergePlan? PlanMergeVolume(Guid volumeId, MergeDirection dir)
        {
            var idx = Volumes.FindIndex(v => v.Id == volumeId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(Volumes, idx - 1, idx, v => v.Id,
                    id => Parts.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((Part)child).VolumeId = winnerId)
                : MergeSiblings(Volumes, idx, idx + 1, v => v.Id,
                    id => Parts.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((Part)child).VolumeId = winnerId);
        }

        public BookMergePlan? PlanMergePart(Guid partId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Parts, partId, p => p.Id);
            var idx = siblings.FindIndex(p => p.Id == partId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(siblings, idx - 1, idx, p => p.Id,
                    id => Chapters.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((Chapter)child).PartId = winnerId)
                : MergeSiblings(siblings, idx, idx + 1, p => p.Id,
                    id => Chapters.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((Chapter)child).PartId = winnerId);
        }

        public BookMergePlan? PlanMergeChapter(Guid chapterId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Chapters, chapterId, c => c.Id);
            var idx = siblings.FindIndex(c => c.Id == chapterId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(siblings, idx - 1, idx, c => c.Id,
                    id => Paragraphs.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((Paragraph)child).ChapterId = winnerId)
                : MergeSiblings(siblings, idx, idx + 1, c => c.Id,
                    id => Paragraphs.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((Paragraph)child).ChapterId = winnerId);
        }

        public BookMergePlan? PlanMergeParagraph(Guid paragraphId, MergeDirection dir)
        {
            var (_, siblings) = FindParentAndSiblings(Paragraphs, paragraphId, p => p.Id);
            var idx = siblings.FindIndex(p => p.Id == paragraphId);
            if (idx < 0) return null;
            return dir == MergeDirection.Previous
                ? MergeSiblings(siblings, idx - 1, idx, p => p.Id,
                    id => Items.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((ParagraphItem)child).ParagraphId = winnerId)
                : MergeSiblings(siblings, idx, idx + 1, p => p.Id,
                    id => Items.TryGetValue(id, out var ch) ? ch.Cast<IBookEntity>().ToList() : [],
                    (child, winnerId) => ((ParagraphItem)child).ParagraphId = winnerId);
        }

        public BookMergePlan? PlanMergeParagraphItem(Guid itemId, MergeDirection dir)
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

            return new BookMergePlan(
                new HierarchyMutation(ToAdd: [], ToDelete: [loser], ToUpdate: [winner]),
                SurvivorId: winner.Id,
                DeletedId: loser.Id);
        }

        private static BookMergePlan? MergeSiblings<TEntity>(
            List<TEntity> siblings,
            int winnerIdx,
            int loserIdx,
            Func<TEntity, Guid> getId,
            Func<Guid, List<IBookEntity>> getChildren,
            Action<IBookEntity, Guid> reassign)
            where TEntity : IBookEntity
        {
            if (winnerIdx < 0 || loserIdx >= siblings.Count) return null;
            var winner = siblings[winnerIdx];
            var loser = siblings[loserIdx];
            var children = getChildren(getId(loser));
            foreach (var child in children) reassign(child, getId(winner));
            return new BookMergePlan(
                new HierarchyMutation(ToAdd: [], ToDelete: [loser!], ToUpdate: children),
                SurvivorId: getId(winner),
                DeletedId: getId(loser));
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
            var toAdd = new List<IBookEntity>();

            if (Volumes.Count > 1)
            {
                var firstVol = Volumes[0];
                var newVol = new Volume
                {
                    Id = Guid.NewGuid(),
                    Title = string.Empty,
                    Order = OrderHelper.GetBefore(firstVol.Order),
                };
                var newPart = new Part
                {
                    Id = Guid.NewGuid(),
                    VolumeId = newVol.Id,
                    Order = OrderHelper.GetBetween(null, null),
                };
                var newChapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = newPart.Id,
                    Order = OrderHelper.GetBetween(null, null),
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
                        Order = OrderHelper.GetBefore(firstPart.Order),
                    };
                    var newChapter = new Chapter
                    {
                        Id = Guid.NewGuid(),
                        PartId = newPart.Id,
                        Order = OrderHelper.GetBetween(null, null),
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
                        Order = OrderHelper.GetBefore(firstChapter?.Order),
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
                    Order = OrderHelper.GetBefore(firstChapter?.Order),
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
                        Order = OrderHelper.GetBefore(firstChapter?.Order),
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
        // PlanPauseInsertions — pure planning for AddPauses.
        // Returns every pause to create; callers pass results to PauseInserter.
        // ---------------------------------------------------------------

        public List<PlannedPause> PlanPauseInsertions()
        {
            var result = new List<PlannedPause>();

            // Paragraph pauses — between adjacent content paragraphs within each chapter.
            foreach (var (chapterId, paragraphs) in Paragraphs)
            {
                var contentParas = paragraphs.Where(p => !IsPauseParagraph(p)).ToList();
                for (int i = 0; i < contentParas.Count - 1; i++)
                {
                    var prev = contentParas[i];
                    var next = contentParas[i + 1];
                    // Skip if a pause paragraph already sits between prev and next.
                    var between = paragraphs
                        .Where(p => string.Compare(p.Order, prev.Order, StringComparison.Ordinal) > 0
                                 && string.Compare(p.Order, next.Order, StringComparison.Ordinal) < 0)
                        .ToList();
                    if (between.Any(IsPauseParagraph)) continue;
                    result.Add(new PlannedPause(chapterId, ParagraphItemType.ParagraphPause, prev.Order, next.Order));
                }
            }

            // Boundary pauses — walk hierarchy, one pause per chapter at boundary.
            for (int vi = 0; vi < Volumes.Count; vi++)
            {
                var vol = Volumes[vi];
                bool isLastVolume = vi == Volumes.Count - 1;
                var parts = Parts.TryGetValue(vol.Id, out var ps) ? ps : [];

                for (int pi2 = 0; pi2 < parts.Count; pi2++)
                {
                    var part = parts[pi2];
                    bool isLastPartInVolume = pi2 == parts.Count - 1;
                    var chapters = Chapters.TryGetValue(part.Id, out var cs) ? cs : [];

                    for (int ci = 0; ci < chapters.Count; ci++)
                    {
                        var chapter = chapters[ci];
                        bool isLastChapterInPart = ci == chapters.Count - 1;

                        ParagraphItemType? pauseType = null;

                        if (isLastChapterInPart && isLastPartInVolume && !isLastVolume)
                            pauseType = ParagraphItemType.VolumePause;
                        else if (isLastChapterInPart && !isLastPartInVolume)
                            pauseType = ParagraphItemType.PartPause;
                        else if (!isLastChapterInPart)
                            pauseType = ParagraphItemType.ChapterPause;

                        if (pauseType == null) continue;

                        // Skip if chapter already ends with a pause of this exact type.
                        var chapterParas = Paragraphs.TryGetValue(chapter.Id, out var cps) ? cps : [];
                        var lastPara = chapterParas.Count > 0 ? chapterParas[^1] : null;
                        if (lastPara != null && IsPauseParagraph(lastPara))
                        {
                            var items = Items.TryGetValue(lastPara.Id, out var its) ? its : [];
                            if (items.Count == 1 && items[0].ItemType == pauseType.Value) continue;
                        }

                        var afterOrder = lastPara?.Order;
                        result.Add(new PlannedPause(chapter.Id, pauseType.Value, afterOrder, null));
                    }
                }
            }

            return result;
        }

        private bool IsPauseParagraph(Paragraph p)
        {
            var items = Items.TryGetValue(p.Id, out var list) ? list : [];
            if (items.Count == 0) return true;
            if (items.Count != 1) return false;
            return ParagraphItemKinds.IsPause(items[0].ItemType);
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
        IReadOnlyList<IBookEntity> ToAdd,
        IReadOnlyList<IBookEntity> ToDelete,
        IReadOnlyList<IBookEntity> ToUpdate);

    /// <summary>
    /// A planned merge: the writes it needs, plus which of the two nodes survived and which was
    /// deleted. The pair is what lets a Book View carry expansion onto the survivor without
    /// re-deriving the sibling order the planner already walked.
    /// </summary>
    public record BookMergePlan(HierarchyMutation Mutation, Guid SurvivorId, Guid DeletedId);

    public record PlannedPause(Guid ChapterId, ParagraphItemType PauseType, string? AfterOrder, string? BeforeOrder);
}
