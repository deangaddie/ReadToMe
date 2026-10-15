using Read2Me.Core.Utils;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;

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
        // The Item insertion rules are shared with PlanInsertNode at a paragraph's
        // start or end (PlanItem): an unknown anchor is NotFound; a pause anchor
        // and blank text are Validation.
        // ---------------------------------------------------------------
        public HierarchyPlan PlanInsertParagraphItem(Guid anchorItemId, InsertPosition position, string? text)
        {
            var (paragraphId, siblings) = FindParentAndSiblings(Items, anchorItemId, i => i.Id);
            if (paragraphId == null) return NoAnchor(NodeKind.ParagraphItem, anchorItemId);

            var idx = siblings.FindIndex(i => i.Id == anchorItemId);
            // Never beside a pause: Speech there is a structure the readers assume cannot exist.
            if (ParagraphItemKinds.IsPause(siblings[idx].ItemType)) return PauseHoldsNoSpeech;

            var placement = position == InsertPosition.Before ? Placement.Before : Placement.After;
            var allowed = StructureRules.For(StructureAnchor.ParagraphItem).Allows(placement)!;
            if (RefuseBlank(allowed, text) is { } blank) return blank;

            var (prev, next) = Beside(siblings, idx, i => i.Order, placement);
            return PlanItem(paragraphId.Value, prev, next, text!);
        }

        // ---------------------------------------------------------------
        // PlanInsertNode: create one node beside or inside the anchor. Only new
        // rows are added — the order key goes between the neighbours, so no
        // existing node is reordered, reparented or removed.
        //
        // What the anchor allows, what each placement creates and whether it needs
        // text are lookups in the Structure rules. A new Volume, Part or Chapter is
        // born empty, its title optional: a blank one leaves it untitled. A new
        // Paragraph is born holding its first Speech item, unattributed: a new item
        // has no speaker yet. A Speech item at a paragraph's start or end follows the
        // Item insertion rules, first or last inside it.
        //
        // Refused: any item anchor (its inserts are InsertParagraphItem's), an anchor
        // the Book does not hold as that kind (NotFound), a placement the anchor's
        // rule does not allow, and blank text where the rule requires it (Validation).
        // ---------------------------------------------------------------
        public HierarchyPlan PlanInsertNode(
            NodeKind anchorKind, Guid anchorId, Placement placement, string? title, string? text)
        {
            if (anchorKind == NodeKind.ParagraphItem)
                return new HierarchyPlan.Refused(BookMutationRejection.Validation,
                    "Nothing can be inserted against an item this way — use InsertParagraphItem beside it.");
            if (!Holds(anchorKind, anchorId)) return NoAnchor(anchorKind, anchorId);

            var anchor = AnchorOf(anchorKind, anchorId);
            if (StructureRules.For(anchor).Allows(placement) is not { } allowed)
                return NotAllowed(anchor, placement);
            if (RefuseBlank(allowed, text) is { } blank) return blank;

            var kind = allowed.Creates;
            var (parentId, prev, next) = kind == anchorKind
                ? SlotBeside(anchorKind, anchorId, placement)
                : SlotInside(anchorKind, anchorId, placement);

            // Anchored on the paragraph rather than an item, so a leading or trailing pause
            // item does not block it.
            if (kind == NodeKind.ParagraphItem) return PlanItem(parentId, prev, next, text!);

            var order = OrderHelper.GetBetween(prev, next);
            var newTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            switch (kind)
            {
                case NodeKind.Volume:
                    var volume = new Volume { Id = Guid.NewGuid(), Title = newTitle ?? string.Empty, Order = order };
                    return Created([volume], new CreatedNode(NodeKind.Volume, volume.Id, null, null));
                case NodeKind.Part:
                    var part = new Part { Id = Guid.NewGuid(), VolumeId = parentId, Title = newTitle, Order = order };
                    return Created([part], new CreatedNode(NodeKind.Part, part.Id, NodeKind.Volume, parentId));
                case NodeKind.Chapter:
                    var chapter = new Chapter { Id = Guid.NewGuid(), PartId = parentId, Title = newTitle, Order = order };
                    return Created([chapter], new CreatedNode(NodeKind.Chapter, chapter.Id, NodeKind.Part, parentId));
                default:
                    var paragraph = new Paragraph { Id = Guid.NewGuid(), ChapterId = parentId, Order = order };
                    var first = UnattributedSpeech(paragraph.Id, OrderHelper.GetBetween(null, null), text!.Trim());
                    return Created([paragraph, first], new CreatedNode(NodeKind.Paragraph, paragraph.Id, NodeKind.Chapter, parentId));
            }
        }

        // ---------------------------------------------------------------
        // PlanDeleteVolume: remove one Volume and, by cascade, all it holds.
        // A book keeps the Structure rules' minimum count of Volumes, so there is
        // always a node to insert from: deleting down past it is refused.
        // ---------------------------------------------------------------
        public HierarchyPlan PlanDeleteVolume(Guid volumeId)
        {
            if (Volumes.Find(v => v.Id == volumeId) is not { } volume)
                return new HierarchyPlan.Refused(BookMutationRejection.NotFound, $"No volume {volumeId} to remove.");
            if (Volumes.Count <= StructureRules.For(StructureAnchor.Volume).MinimumCount)
                return new HierarchyPlan.Refused(BookMutationRejection.Validation, "A book must keep at least one volume.");
            return new HierarchyPlan.Planned(new HierarchyMutation(ToAdd: [], ToDelete: [volume], ToUpdate: []), Created: null);
        }

        // The new item of the Item insertion rules, written once for both item routes; the anchor
        // and the text are already checked. It stays within paragraphId, between prev and next, and
        // is born unattributed — no CharacterId, no VoiceInstructions, no AudioFileName — because
        // the anchor held two speakers by construction, and an inherited speaker would be a
        // confident wrong answer that never reaches the attribution queue.
        private static HierarchyPlan PlanItem(Guid paragraphId, string? prev, string? next, string text)
        {
            var item = UnattributedSpeech(paragraphId, OrderHelper.GetBetween(prev, next), text.Trim());
            return Created([item], new CreatedNode(NodeKind.ParagraphItem, item.Id, NodeKind.Paragraph, paragraphId));
        }

        // The Structure rules entry a held anchor plans from: a pause paragraph has its own.
        private StructureAnchor AnchorOf(NodeKind kind, Guid anchorId) => kind switch
        {
            NodeKind.Volume => StructureAnchor.Volume,
            NodeKind.Part => StructureAnchor.Part,
            NodeKind.Chapter => StructureAnchor.Chapter,
            NodeKind.Paragraph => IsPauseParagraph(anchorId) ? StructureAnchor.PauseParagraph : StructureAnchor.Paragraph,
            _ => StructureAnchor.ParagraphItem,
        };

        private static readonly HierarchyPlan.Refused PauseHoldsNoSpeech = new(BookMutationRejection.Validation,
            "A pause holds no speech — anchor on a speech item, or insert a paragraph before or after the pause.");

        // The one placement the table turns down for a held anchor today is a speech item at a
        // pause paragraph's start or end.
        private static HierarchyPlan.Refused NotAllowed(StructureAnchor anchor, Placement placement) =>
            anchor == StructureAnchor.PauseParagraph
                ? PauseHoldsNoSpeech
                : new(BookMutationRejection.Validation, $"A {anchor} takes no insert {placement}.");

        // Blank text where the placement's rule requires it: an empty paragraph reads as a pause,
        // and whitespace alone is not an item.
        private static HierarchyPlan.Refused? RefuseBlank(StructurePlacement allowed, string? text)
        {
            if (!allowed.TextRequired || !string.IsNullOrWhiteSpace(text)) return null;
            return new(BookMutationRejection.Validation, allowed.Creates == NodeKind.Paragraph
                ? "A new paragraph needs text — an empty paragraph reads as a pause."
                : "An inserted item needs text — whitespace alone is not an item.");
        }

        private static HierarchyPlan.Planned Created(IBookEntity[] added, CreatedNode created) =>
            new(new HierarchyMutation(ToAdd: added, ToDelete: [], ToUpdate: []), created);

        private static HierarchyPlan.Refused NoAnchor(NodeKind kind, Guid id) =>
            new(BookMutationRejection.NotFound, $"No {KindName(kind)} {id} to insert against.");

        private static string KindName(NodeKind kind) =>
            kind == NodeKind.ParagraphItem ? "paragraph item" : kind.ToString().ToLowerInvariant();

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

        // Whether the Book holds anchorId as a node of that kind.
        private bool Holds(NodeKind kind, Guid anchorId) => kind switch
        {
            NodeKind.Volume => Volumes.Exists(v => v.Id == anchorId),
            NodeKind.Part => Holds(Parts, anchorId, p => p.Id),
            NodeKind.Chapter => Holds(Chapters, anchorId, c => c.Id),
            NodeKind.Paragraph => Holds(Paragraphs, anchorId, p => p.Id),
            _ => Holds(Items, anchorId, i => i.Id),
        };

        // Insert slots: the parent a new node goes under and the order keys it
        // goes between. The anchor is known to exist.

        // A sibling of the anchor, Before or After it.
        private (Guid, string?, string?) SlotBeside(NodeKind anchorKind, Guid anchorId, Placement placement) =>
            anchorKind switch
            {
                NodeKind.Volume => VolumeSlot(anchorId, placement),
                NodeKind.Part => SlotBeside(Parts, anchorId, p => p.Id, p => p.Order, placement),
                NodeKind.Chapter => SlotBeside(Chapters, anchorId, c => c.Id, c => c.Order, placement),
                _ => SlotBeside(Paragraphs, anchorId, p => p.Id, p => p.Order, placement),
            };

        // A child of the anchor, AtStart or AtEnd of it.
        private (Guid, string?, string?) SlotInside(NodeKind anchorKind, Guid anchorId, Placement placement) =>
            anchorKind switch
            {
                NodeKind.Volume => SlotInside(anchorId, Parts, p => p.Order, placement),
                NodeKind.Part => SlotInside(anchorId, Chapters, c => c.Order, placement),
                NodeKind.Chapter => SlotInside(anchorId, Paragraphs, p => p.Order, placement),
                _ => SlotInside(anchorId, Items, i => i.Order, placement),
            };

        // Volumes have no parent row; Guid.Empty stands in and is never read.
        private (Guid, string?, string?) VolumeSlot(Guid anchorId, Placement placement)
        {
            var (prev, next) = Beside(Volumes, Volumes.FindIndex(v => v.Id == anchorId), v => v.Order, placement);
            return (Guid.Empty, prev, next);
        }

        private static (Guid, string?, string?) SlotBeside<T>(
            Dictionary<Guid, List<T>> byParent, Guid anchorId, Func<T, Guid> getId, Func<T, string> getOrder, Placement placement)
        {
            var (parentId, siblings) = FindParentAndSiblings(byParent, anchorId, getId);
            var (prev, next) = Beside(siblings, siblings.FindIndex(s => getId(s) == anchorId), getOrder, placement);
            return (parentId!.Value, prev, next);
        }

        private static (Guid, string?, string?) SlotInside<T>(
            Guid anchorId, Dictionary<Guid, List<T>> childrenByParent, Func<T, string> getOrder, Placement placement)
        {
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

        private bool IsPauseParagraph(Paragraph p) => IsPauseParagraph(p.Id);

        private bool IsPauseParagraph(Guid paragraphId)
        {
            var items = Items.TryGetValue(paragraphId, out var list) ? list : [];
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
    /// How the Book Hierarchy answers an insert or a Volume delete. The three cases are exhaustive:
    /// a refusal names its kind and reason, so a caller maps it without re-deriving why.
    /// </summary>
    public abstract record HierarchyPlan
    {
        private HierarchyPlan() { }

        /// <summary>The writes, and the node they create (null for a delete).</summary>
        public sealed record Planned(HierarchyMutation Mutation, CreatedNode? Created) : HierarchyPlan;

        /// <summary>A valid request that changes nothing.</summary>
        public sealed record NoChange : HierarchyPlan;

        /// <summary>A request the Book refuses: an unknown anchor, or one the rules forbid.</summary>
        public sealed record Refused(BookMutationRejection Rejection, string Reason) : HierarchyPlan;
    }

    /// <summary>
    /// The node a plan creates and the node it hangs under. A Volume has no parent node, so its
    /// parent kind and id are null.
    /// </summary>
    public record CreatedNode(NodeKind Kind, Guid Id, NodeKind? ParentKind, Guid? ParentId);

    /// <summary>
    /// A planned merge: the writes it needs, plus which of the two nodes survived and which was
    /// deleted. The pair is what lets a Book View carry expansion onto the survivor without
    /// re-deriving the sibling order the planner already walked.
    /// </summary>
    public record BookMergePlan(HierarchyMutation Mutation, Guid SurvivorId, Guid DeletedId);

    public record PlannedPause(Guid ChapterId, ParagraphItemType PauseType, string? AfterOrder, string? BeforeOrder);
}
